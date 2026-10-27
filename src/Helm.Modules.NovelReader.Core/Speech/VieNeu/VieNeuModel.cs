using System.Numerics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// VieNeu-TTS v3 Turbo in ONNX Runtime, on the CPU (a C# port of VieNeu's torch-free engine, onnx_runtime_lite.py):
/// phonemes and a preset voice in, 48 kHz mono sound out. The backbone (12 layers) reads the prompt once and then one
/// frame at a time with its key/value cache; for each frame a one-layer acoustic decoder picks 16 codes, one per
/// codebook, each from the one before; the MOSS codec turns the frames into sound. The embedding tables, the speaker
/// anchor and the sampling are done here. Safe to use from several threads at once: ONNX Runtime sessions are, and
/// everything else here is read-only.
/// </summary>
public sealed class VieNeuModel : IDisposable
{
    public const int SampleRate = 48000;

    private readonly InferenceSession _prefill;
    private readonly InferenceSession _decode;
    private readonly InferenceSession _acoustic;
    private readonly InferenceSession _codec;
    private readonly float[] _textEmbedding;
    private readonly float[] _audioEmbedding;
    private readonly float[] _xvecWeight;
    private readonly float[] _xvecBias;
    private readonly float[] _xvecNormWeight;
    private readonly float[] _xvecNormBias;
    private readonly float _xvecNormEps;
    private readonly PhonemeTokenizer _tokenizer;
    private readonly Config _config;

    private VieNeuModel(string modelFolder, string codecFolder, int threads)
    {
        _config = Config.Read(Path.Combine(modelFolder, "config.json"));
        _tokenizer = PhonemeTokenizer.Load(Path.Combine(modelFolder, "tokenizer.json"));
        var heads = NpyArchive.Read(Path.Combine(modelFolder, "vieneu_v3_heads.npz"));
        _textEmbedding = heads["text_emb"].Data;
        _audioEmbedding = heads["audio_emb"].Data;
        _xvecWeight = heads["xvec_w"].Data;
        _xvecBias = heads["xvec_b"].Data;
        _xvecNormWeight = heads["xvec_ln_w"].Data;
        _xvecNormBias = heads["xvec_ln_b"].Data;
        _xvecNormEps = heads["xvec_ln_eps"].Data[0];
        _audioVocab = heads["audio_emb"].Shape[1];

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = threads,
        };
        // Threads waiting between operators sleep instead of spinning: several sentences are made at once.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _prefill = new InferenceSession(Path.Combine(modelFolder, "vieneu_prefill.onnx"), options);
        _decode = new InferenceSession(Path.Combine(modelFolder, "vieneu_decode_step.onnx"), options);
        _acoustic = new InferenceSession(Path.Combine(modelFolder, "vieneu_acoustic_cached.onnx"), options);
        _codec = new InferenceSession(Path.Combine(codecFolder, "moss_audio_tokenizer_decode_full.onnx"), options);
        _backboneCache = CacheOutputs(_prefill, _config.Layers);
        if (!_backboneCache.SequenceEqual(CacheOutputs(_decode, _config.Layers)))
            throw new InvalidDataException("VieNeu's prefill and decode graphs disagree on their outputs.");
        _acousticCache = CacheOutputs(_acoustic, _config.LocalLayers);
    }

    private readonly int _audioVocab;

    /// <summary>Where each layer's key and value cache is among a graph's outputs: keys of layers 0…n-1, then values (output 0 is the hidden state).</summary>
    private readonly int[] _backboneCache;

    private readonly int[] _acousticCache;

    private static int[] CacheOutputs(InferenceSession session, int layers)
    {
        var names = session.OutputNames.ToList();
        if (names.IndexOf("hidden") != 0) throw new InvalidDataException("A VieNeu graph does not start with its hidden state.");
        int Find(string name) => names.IndexOf(name) is >= 0 and var at ? at : throw new InvalidDataException($"A VieNeu graph has no {name}.");
        return Enumerable.Range(0, layers).Select(i => Find($"present_k_{i}"))
            .Concat(Enumerable.Range(0, layers).Select(i => Find($"present_v_{i}")))
            .ToArray();
    }

    /// <summary>Tests only: always the best code instead of sampling, to compare with VieNeu's own engine code for code.</summary>
    internal bool Greedy { get; set; }

    /// <summary>The codes of the last frames made, for tests that compare them.</summary>
    internal IReadOnlyList<int[]>? LastFrames { get; private set; }

    /// <summary>Loads the model; <paramref name="threads"/> is the CPU threads one sentence may use.</summary>
    public static VieNeuModel Load(string modelFolder, string codecFolder, int threads) => new(modelFolder, codecFolder, threads);

    /// <summary>
    /// The sound of <paramref name="phonemes"/> (sea-g2p's output for one sentence) in <paramref name="voice"/>. A short
    /// sentence that seems to have run on is made again, up to <see cref="BabbleGuard.Retries"/> times.
    /// </summary>
    public float[] Synthesize(string phonemes, VieNeuVoicePreset voice, Random random, CancellationToken ct) =>
        Synthesize(phonemes, voice, SpeakerAnchor(voice.SpeakerEmbedding), random, ct);

    /// <summary>
    /// As above with the speaker anchor given: tests pass VieNeu's own (NumPy sums in another order, one unit in the last
    /// place apart, and the int8 backbone turns that into other codes).
    /// </summary>
    internal float[] Synthesize(string phonemes, VieNeuVoicePreset voice, float[] anchor, Random random, CancellationToken ct)
    {
        var cap = BabbleGuard.FrameCap(phonemes);
        var prompt = BuildPrompt(phonemes, voice.ReferenceCodes, anchor);

        var frames = Generate(prompt, anchor, cap, random, ct);
        LastFrames = frames;
        if (frames.Count == 0) return [];
        var wav = Decode(frames);
        var best = BabbleGuard.Check(wav, SampleRate, phonemes, cap, frames.Count);
        for (var tries = 0; best.Suspect && tries < BabbleGuard.Retries; tries++)
        {
            var again = Generate(prompt, anchor, cap, random, ct);
            if (again.Count == 0) continue;
            var other = Decode(again);
            var check = BabbleGuard.Check(other, SampleRate, phonemes, cap, again.Count);
            if (!BabbleGuard.Better(check, best)) continue;
            wav = other;
            best = check;
        }
        return wav;
    }

    // ---- The prompt ------------------------------------------------------------------------------------------------

    /// <summary>The 192-value speaker embedding projected to the model's width and layer-normalized, added to every row.</summary>
    private float[] SpeakerAnchor(float[] embedding)
    {
        var hidden = _config.Hidden;
        var width = embedding.Length;
        var v = new float[hidden];
        for (var h = 0; h < hidden; h++) v[h] = Dot(_xvecWeight.AsSpan(h * width, width), embedding) + _xvecBias[h];
        var mean = v.Average();
        var variance = v.Select(x => (x - mean) * (x - mean)).Average();
        var scale = 1 / MathF.Sqrt(variance + _xvecNormEps);
        for (var h = 0; h < hidden; h++) v[h] = (v[h] - mean) * scale * _xvecNormWeight[h] + _xvecNormBias[h];
        return v;
    }

    /// <summary>
    /// Style, prompt start, the phoneme tokens, prompt end; then one row per frame of the voice's reference speech. Each
    /// row is a text token plus 16 audio codes (or padding), embedded and summed.
    /// </summary>
    private float[] BuildPrompt(string phonemes, long[][] reference, float[] anchor)
    {
        var text = new List<int> { _config.DefaultStyle, _config.TextPromptStart };
        text.AddRange(_tokenizer.Encode(phonemes));
        text.Add(_config.TextPromptEnd);
        var hidden = _config.Hidden;
        var rows = text.Count + reference.Length;
        var embeds = new float[rows * hidden];
        for (var r = 0; r < text.Count; r++) EmbedRow(embeds.AsSpan(r * hidden, hidden), text[r], null, anchor);
        for (var r = 0; r < reference.Length; r++)
            EmbedRow(embeds.AsSpan((text.Count + r) * hidden, hidden), _config.AudioRefSlot, reference[r], anchor);
        return embeds;
    }

    private void EmbedRow(Span<float> row, int textToken, IReadOnlyList<long>? codes, float[] anchor)
    {
        var hidden = _config.Hidden;
        _textEmbedding.AsSpan(textToken * hidden, hidden).CopyTo(row);
        if (codes is not null)
        {
            for (var ch = 0; ch < _config.Codebooks; ch++)
            {
                var code = (int)codes[ch];
                if (code == _config.AudioPad) continue;
                Add(row, AudioRow(ch, code));
            }
        }
        Add(row, anchor);
    }

    private ReadOnlySpan<float> AudioRow(int codebook, int code)
    {
        var hidden = _config.Hidden;
        return _audioEmbedding.AsSpan((codebook * _audioVocab + code) * hidden, hidden);
    }

    // ---- Frames ----------------------------------------------------------------------------------------------------

    /// <summary>The prompt read once, then frame after frame until the model says the speech ended or the cap is reached.</summary>
    private List<int[]> Generate(float[] prompt, float[] anchor, int cap, Random random, CancellationToken ct)
    {
        var hidden = _config.Hidden;
        var layers = _config.Layers;
        var promptRows = prompt.Length / hidden;
        var sampler = new CodeSampler(_config.Codebooks, random, Greedy);
        var frames = new List<int[]>();
        using var run = new RunOptions();
        using var registration = ct.Register(() => run.Terminate = true);

        IDisposableReadOnlyCollection<OrtValue>? cache = null;
        try
        {
            float[] state;
            using (var input = OrtValue.CreateTensorValueFromMemory(prompt, [1, promptRows, hidden]))
            {
                cache = _prefill.Run(run, ["inputs_embeds"], [input], _prefill.OutputNames);
                state = LastRow(cache[0], hidden);
            }
            var slot = new float[hidden];
            var position = new long[1];
            for (var t = 0; t < cap; t++)
            {
                ct.ThrowIfCancellationRequested();
                var (codes, end) = Frame(state, sampler, run);
                frames.Add(codes);
                if (end) break;

                Array.Clear(slot);
                EmbedRow(slot, _config.SpeechStart, codes.Select(c => (long)c).ToArray(), anchor);
                position[0] = promptRows + t;
                var names = new List<string>(2 + 2 * layers) { "inputs_embeds", "position_ids" };
                var values = new List<OrtValue>(2 + 2 * layers);
                using var slotValue = OrtValue.CreateTensorValueFromMemory(slot, [1, 1, hidden]);
                using var positionValue = OrtValue.CreateTensorValueFromMemory(position, [1, 1]);
                values.Add(slotValue);
                values.Add(positionValue);
                for (var i = 0; i < layers; i++)
                {
                    names.Add($"past_k_{i}");
                    values.Add(cache[_backboneCache[i]]);
                }
                for (var i = 0; i < layers; i++)
                {
                    names.Add($"past_v_{i}");
                    values.Add(cache[_backboneCache[layers + i]]);
                }
                var next = _decode.Run(run, names, values, _decode.OutputNames);
                cache.Dispose();
                cache = next;
                state = LastRow(cache[0], hidden);
            }
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            cache?.Dispose();
        }
        return frames;
    }

    /// <summary>
    /// One frame: the acoustic decoder reads the backbone's state and the speech-start token (whose output also says
    /// whether the speech ended), then picks the 16 codes, feeding each one back for the next codebook.
    /// </summary>
    private (int[] Codes, bool End) Frame(float[] state, CodeSampler sampler, RunOptions run)
    {
        var hidden = _config.Hidden;
        var codebooks = _config.Codebooks;
        var codes = new int[codebooks];
        var logits = new float[_audioVocab];

        var first = new float[2 * hidden];
        state.CopyTo(first, 0);
        _textEmbedding.AsSpan(_config.SpeechStart * hidden, hidden).CopyTo(first.AsSpan(hidden));
        var localLayers = _config.LocalLayers;
        var heads = _config.LocalHeads;
        var headWidth = hidden / heads;

        IDisposableReadOnlyCollection<OrtValue> output;
        using (var tokens = OrtValue.CreateTensorValueFromMemory(first, [1, 2, hidden]))
        using (var positions = OrtValue.CreateTensorValueFromMemory(new long[] { 0, 1 }, [1, 2]))
        {
            var names = new List<string> { "token_emb", "position_ids" };
            var values = new List<OrtValue> { tokens, positions };
            // The first step has no cache yet: empty tensors with no rows.
            var empties = new List<OrtValue>();
            try
            {
                for (var i = 0; i < localLayers; i++)
                {
                    foreach (var name in (string[])[$"past_k_{i}", $"past_v_{i}"])
                    {
                        var empty = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, [1, heads, 0, headWidth]);
                        empties.Add(empty);
                        names.Add(name);
                        values.Add(empty);
                    }
                }
                output = _acoustic.Run(run, names, values, _acoustic.OutputNames);
            }
            finally
            {
                foreach (var empty in empties) empty.Dispose();
            }
        }

        bool end;
        try
        {
            var outHidden = output[0].GetTensorDataAsSpan<float>();
            end = TextArgMax(outHidden[..hidden]) == _config.SpeechEnd;
            codes[0] = SampleCode(0, outHidden.Slice(hidden, hidden), logits, sampler);
            for (var ch = 1; ch < codebooks; ch++)
            {
                var embedding = AudioRow(ch - 1, codes[ch - 1]).ToArray();
                using var token = OrtValue.CreateTensorValueFromMemory(embedding, [1, 1, hidden]);
                using var position = OrtValue.CreateTensorValueFromMemory(new long[] { ch + 1 }, [1, 1]);
                var names = new List<string> { "token_emb", "position_ids" };
                var values = new List<OrtValue> { token, position };
                for (var i = 0; i < localLayers; i++)
                {
                    names.Add($"past_k_{i}");
                    values.Add(output[_acousticCache[i]]);
                    names.Add($"past_v_{i}");
                    values.Add(output[_acousticCache[localLayers + i]]);
                }
                var next = _acoustic.Run(run, names, values, _acoustic.OutputNames);
                output.Dispose();
                output = next;
                codes[ch] = SampleCode(ch, output[0].GetTensorDataAsSpan<float>()[..hidden], logits, sampler);
            }
        }
        finally
        {
            output.Dispose();
        }
        return (codes, end);
    }

    /// <summary>Scores every code of a codebook against <paramref name="vector"/> (the tied embedding is the output head) and samples one.</summary>
    private int SampleCode(int codebook, ReadOnlySpan<float> vector, float[] logits, CodeSampler sampler)
    {
        for (var code = 0; code < _audioVocab; code++) logits[code] = Dot(AudioRow(codebook, code), vector);
        return sampler.Sample(codebook, logits);
    }

    private int TextArgMax(ReadOnlySpan<float> vector)
    {
        var hidden = _config.Hidden;
        var best = 0;
        var bestScore = float.NegativeInfinity;
        for (var token = 0; token < _config.TextVocab; token++)
        {
            var score = Dot(_textEmbedding.AsSpan(token * hidden, hidden), vector);
            if (score > bestScore)
            {
                bestScore = score;
                best = token;
            }
        }
        return best;
    }

    private static float[] LastRow(OrtValue value, int hidden)
    {
        var data = value.GetTensorDataAsSpan<float>();
        return data[^hidden..].ToArray();
    }

    // ---- Sound -----------------------------------------------------------------------------------------------------

    /// <summary>The codec's sound for the frames: its two channels averaged to one.</summary>
    private float[] Decode(List<int[]> frames)
    {
        var codebooks = _config.Codebooks;
        var codes = new int[frames.Count * codebooks];
        for (var f = 0; f < frames.Count; f++) frames[f].CopyTo(codes, f * codebooks);
        using var codesValue = OrtValue.CreateTensorValueFromMemory(codes, [1, frames.Count, codebooks]);
        using var lengthValue = OrtValue.CreateTensorValueFromMemory(new[] { frames.Count }, [1]);
        using var run = new RunOptions();
        using var output = _codec.Run(run, ["audio_codes", "audio_code_lengths"], [codesValue, lengthValue], _codec.OutputNames);
        var shape = output[0].GetTensorTypeAndShape().Shape;
        var channels = (int)shape[1];
        var length = (int)shape[2];
        var audio = output[0].GetTensorDataAsSpan<float>();
        var mono = new float[length];
        for (var c = 0; c < channels; c++)
        {
            var channel = audio.Slice(c * length, length);
            for (var i = 0; i < length; i++) mono[i] += channel[i];
        }
        if (channels > 1)
            for (var i = 0; i < length; i++) mono[i] /= channels;
        return mono;
    }

    // ---- Maths -----------------------------------------------------------------------------------------------------

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        var width = Vector<float>.Count;
        for (; i <= a.Length - width; i += width) sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        var total = Vector.Sum(sum);
        for (; i < a.Length; i++) total += a[i] * b[i];
        return total;
    }

    private static void Add(Span<float> target, ReadOnlySpan<float> source)
    {
        var i = 0;
        var width = Vector<float>.Count;
        for (; i <= target.Length - width; i += width) (new Vector<float>(target[i..]) + new Vector<float>(source[i..])).CopyTo(target[i..]);
        for (; i < target.Length; i++) target[i] += source[i];
    }

    public void Dispose()
    {
        _prefill.Dispose();
        _decode.Dispose();
        _acoustic.Dispose();
        _codec.Dispose();
    }

    /// <summary>The token ids and sizes from the model's config.json.</summary>
    private sealed record Config(int Hidden, int Layers, int LocalLayers, int LocalHeads, int Codebooks, int AudioPad, int TextVocab,
        int TextPromptStart, int TextPromptEnd, int SpeechStart, int SpeechEnd, int AudioRefSlot, int DefaultStyle)
    {
        public static Config Read(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var c = document.RootElement;
            int Get(string name, int fallback = -1) =>
                c.TryGetProperty(name, out var v) ? v.GetInt32() : fallback >= 0 ? fallback : throw new InvalidDataException($"config.json has no {name}.");
            return new Config(Get("hidden_size"), Get("num_hidden_layers"), Get("local_num_hidden_layers", 1), Get("local_num_attention_heads", 8),
                Get("n_vq"), Get("audio_pad_token_id"), Get("text_vocab_size"), Get("text_prompt_start_token_id"),
                Get("text_prompt_end_token_id"), Get("speech_generation_start_token_id"), Get("speech_generation_end_token_id"),
                Get("audio_ref_slot_token_id"), Get("default_style_token_id", 16));
        }
    }
}
