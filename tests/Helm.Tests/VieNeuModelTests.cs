using System.Diagnostics;
using System.Text.Json;
using Helm.Modules.NovelReader.Speech.VieNeu;
using Xunit;
using Xunit.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The VieNeu voice that runs in Helm: its preset voices, its guards, and (where the model is on this PC) the same codes,
/// frame for frame, as VieNeu's own ONNX engine when both always take the best code.
/// </summary>
public sealed class VieNeuModelTests(ITestOutputHelper output)
{
    /// <summary>The model's files: HELM_VIENEU_MODELS, else where they were downloaded by hand. CI has neither.</summary>
    private static string? ModelRoot()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("HELM_VIENEU_MODELS"), @"C:\tmp\vieneu-models" })
            if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "v3", "vieneu_prefill.onnx"))) return root;
        return null;
    }

    [Fact]
    public void Only_the_womens_voices_are_offered_the_editors_picks_first_and_Ngoc_Huyen_is_the_default()
    {
        var voices = VieNeuVoices.All;

        Assert.Equal(11, voices.Count);
        Assert.All(voices, v => Assert.StartsWith("Nữ", v.Description));
        Assert.Equal(["truc_ly", "mai_anh", "thuy_dung", "ngoc_huyen", "ngoc_tran"], voices.Take(5).Select(v => v.Id));
        Assert.All(voices, v => Assert.Equal(192, v.SpeakerEmbedding.Length));
        Assert.All(voices, v => Assert.All(v.ReferenceCodes, frame => Assert.Equal(16, frame.Length)));
        Assert.Equal("Ngọc Huyền", VieNeuVoices.Find(VieNeuVoices.DefaultId)?.Name);
    }

    [Fact]
    public void A_preset_never_ends_on_the_encoders_padding_click()
    {
        Assert.All(VieNeuVoices.All, v => Assert.NotEqual(455, v.ReferenceCodes[^1][0]));
    }

    [Fact]
    public void A_short_sentence_gets_about_a_second_per_syllable_and_a_long_one_two_frames_a_phoneme()
    {
        // "Được." is one syllable; VieNeu allows 13 frames (about a second).
        Assert.Equal(1, BabbleGuard.Syllables("ɗˈyə6c."));
        Assert.Equal(13, BabbleGuard.FrameCap("ɗˈyə6c."));
        // "quá" is one syllable even though its tone mark is a vowel letter.
        Assert.Equal(1, BabbleGuard.Syllables("kwˈaːɜ"));
        // An English word counts its vowel groups.
        Assert.Equal(3, BabbleGuard.Syllables("kəmpjˈuːɾɚ"));
        var sentence = "lˈəm fˈɔŋ ŋˈə4ŋ ɗˈə2w ɲˈi2n bˈə2w tʃˈəː2j ɗˈəɪ2 sˈaːw, tʃˈɔŋ lˈɔ2ŋ zˈəŋ lˈen mˈo6t̪ kˈaː4m zˈaːɜc xˈɔɜ t̪ˈaː4.";
        Assert.Equal(Math.Min(300, 24 + 2 * sentence.Length), BabbleGuard.FrameCap(sentence));
    }

    [Fact]
    public void Bursts_of_sound_are_counted_apart_only_across_a_real_gap()
    {
        const int rate = 48000;
        var wav = new float[rate];
        void Tone(double from, double to)
        {
            for (var i = (int)(from * rate); i < (int)(to * rate); i++) wav[i] = 0.5f * MathF.Sin(i * 0.05f);
        }
        Tone(0.10, 0.30);
        Tone(0.33, 0.40); // 30 ms later: the same burst
        Tone(0.60, 0.80); // 200 ms later: a new one

        Assert.Equal(2, BabbleGuard.SpeechBursts(wav, rate));
    }

    [Fact]
    public void With_the_best_code_always_taken_the_model_makes_the_same_frames_as_VieNeus_own_engine()
    {
        if (ModelRoot() is not { } root)
        {
            output.WriteLine("The VieNeu model is not on this PC: skipped.");
            return;
        }
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine("Fixtures", "vieneu", "greedy-ngoc-huyen.json")));
        var tokenizer = PhonemeTokenizer.Load(Path.Combine(root, "v3", "tokenizer.json"));
        var clock = Stopwatch.StartNew();
        using var model = VieNeuModel.Load(Path.Combine(root, "v3"), Path.Combine(root, "codec"), threads: 6);
        model.Greedy = true;
        output.WriteLine($"Loaded in {clock.ElapsedMilliseconds} ms");
        var voice = VieNeuVoices.Find("ngoc_huyen")!;

        foreach (var c in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var phonemes = c.GetProperty("phonemes").GetString()!;
            Assert.Equal(c.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()), tokenizer.Encode(phonemes));

            clock.Restart();
            var anchor = fixture.RootElement.GetProperty("anchor").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var wav = model.Synthesize(phonemes, voice, anchor, new Random(0), CancellationToken.None);
            var took = clock.Elapsed.TotalSeconds;
            var expected = c.GetProperty("frames").EnumerateArray().Select(f => f.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToList();
            var frames = model.LastFrames!;
            var same = 0;
            while (same < Math.Min(frames.Count, expected.Count) && frames[same].SequenceEqual(expected[same])) same++;
            output.WriteLine($"{c.GetProperty("text").GetString()}: {frames.Count} frames ({expected.Count} expected), the first {same} the same; " +
                             $"{wav.Length / 48000.0:0.00} s of sound in {took:0.00} s");
            Assert.Equal(expected.Count, frames.Count);
            Assert.Equal(expected.Count, same);
            Assert.Equal(c.GetProperty("samples").GetInt32(), wav.Length);
        }
    }
}
