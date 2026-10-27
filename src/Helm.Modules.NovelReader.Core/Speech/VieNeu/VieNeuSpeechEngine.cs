using Helm.Speech.Vietnamese;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// The VieNeu voices (women's, Vietnamese) that run in Helm itself: no other app, no server, offline, nothing leaves the
/// device. Shown once the voice is downloaded (<see cref="VieNeuInstaller"/>). The model is loaded with the first
/// sentence and let go after <see cref="IdleUnload"/> without one (it holds about 300 MB). Each sentence: sea-g2p's
/// phonemes (a long sentence in parts), the model's 48 kHz sound, halved to 24 kHz, then the speed and the volume
/// (the model speaks at one speed: <see cref="TimeStretch"/>).
/// </summary>
public sealed class VieNeuSpeechEngine : ISpeechEngine, IDisposable
{
    public const string EnginePrefix = "vieneu";

    public const int SampleRate = VieNeuModel.SampleRate / 2;

    /// <summary>A sentence longer than this is read in parts, split at a comma near the middle (VieNeu reads up to about 256).</summary>
    internal const int MaxPart = 200;

    /// <summary>The pause between the parts of a long sentence.</summary>
    private static readonly TimeSpan PartPause = TimeSpan.FromMilliseconds(120);

    internal static TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(5);

    private readonly VieNeuInstaller _installer;
    private readonly ILogger<VieNeuSpeechEngine> _logger;
    private readonly object _gate = new();
    private readonly Timer _idle;
    private Task<Loaded>? _loading;
    private int _users;
    private long _lastUse;

    public VieNeuSpeechEngine(VieNeuInstaller installer, ILogger<VieNeuSpeechEngine> logger)
    {
        _installer = installer;
        _logger = logger;
        _idle = new Timer(_ => UnloadIfIdle());
        installer.InstalledChanged += (_, _) => Release();
    }

    public string Prefix => EnginePrefix;

    /// <summary>
    /// Sentences made ahead at once. One sentence uses few threads well (its frames come one after another), so several
    /// at once go further: on a 16-thread PC three at a time made sound about 6.5 times faster than it plays, one at a
    /// time with six threads only 2.4 times.
    /// </summary>
    public int Parallel => Environment.ProcessorCount >= 12 ? 3 : 2;

    /// <summary>CPU threads for one sentence: the cores shared by the sentences made ahead and the one to play now.</summary>
    internal int ThreadsPerSentence => Math.Clamp(Environment.ProcessorCount / (Parallel + 1), 1, 4);

    public IReadOnlyList<SpeechVoice> Voices => _installer.Files.IsInstalled ? AllVoices : [];

    public static IReadOnlyList<SpeechVoice> AllVoices { get; } = VieNeuVoices.All
        .Select(v => new SpeechVoice($"{EnginePrefix}:{v.Id}", v.Name, "vi-VN", IsOnline: false) { IsFemale = true, Description = v.Description })
        .ToList();

    /// <summary>The voice reading starts with.</summary>
    public static string DefaultVoiceId => $"{EnginePrefix}:{VieNeuVoices.DefaultId}";

    public void RefreshVoices() { }

    /// <summary>Loads the model ahead of the first sentence (when a chapter is opened), so reading starts quickly.</summary>
    public void WarmUp()
    {
        if (!_installer.Files.IsInstalled) return;
        _ = LoadAsync().ContinueWith(t => _logger.LogWarning(t.Exception, "The VieNeu voice could not be loaded"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public async Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct)
    {
        if (!_installer.Files.IsInstalled)
            throw new SpeechUnavailableException("The VieNeu voice is not downloaded. Download it in Novel Reader's settings.");
        if (VieNeuVoices.Find(voice.EngineVoice) is not { } preset)
            throw new SpeechUnavailableException($"The voice {voice.Name} is not one of VieNeu's.");

        Loaded loaded;
        lock (_gate) _users++;
        try
        {
            try
            {
                loaded = await LoadAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or DllNotFoundException)
            {
                throw new SpeechUnavailableException("The VieNeu voice could not be loaded: " + ex.Message, ex);
            }
            var samples = await Task.Run(() => Speak(loaded, text, preset, ct), ct).ConfigureAwait(false);
            var stretched = TimeStretch.Scale(TimeStretch.Stretch(samples, SampleRate, options.Rate), options.Volume);
            return new SpeechAudio(TimeStretch.Wav(stretched, SampleRate), "audio/wav");
        }
        finally
        {
            lock (_gate)
            {
                _users--;
                _lastUse = Environment.TickCount64;
            }
            _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
        }
    }

    private static short[] Speak(Loaded loaded, string text, VieNeuVoicePreset preset, CancellationToken ct)
    {
        var pause = new short[(int)(SampleRate * PartPause.TotalSeconds)];
        var parts = new List<short[]>();
        foreach (var part in SplitLong(text))
        {
            var phonemes = loaded.Phonemizer.Phonemize(part);
            if (BabbleGuard.Syllables(phonemes) == 0) continue;
            if (parts.Count > 0) parts.Add(pause);
            parts.Add(HalfRate.Convert(loaded.Model.Synthesize(phonemes, preset, Random.Shared, ct)));
        }
        return parts.SelectMany(p => p).ToArray();
    }

    /// <summary>
    /// <paramref name="text"/> in parts of at most <see cref="MaxPart"/> characters: cut at the punctuation nearest the
    /// middle (a comma, a semicolon, a colon, a dash), else at the space nearest it.
    /// </summary>
    internal static IEnumerable<string> SplitLong(string text)
    {
        text = text.Trim();
        if (text.Length <= MaxPart)
        {
            if (text.Length > 0) yield return text;
            yield break;
        }
        var middle = text.Length / 2;
        var cut = Nearest(text, middle, c => c is ',' or ';' or ':' or '—' or '–', after: true)
            ?? Nearest(text, middle, char.IsWhiteSpace, after: false)
            ?? middle;
        foreach (var part in SplitLong(text[..cut])) yield return part;
        foreach (var part in SplitLong(text[cut..])) yield return part;
    }

    /// <summary>The cut closest to <paramref name="middle"/> at a character that matches (just after it when <paramref name="after"/>).</summary>
    private static int? Nearest(string text, int middle, Func<char, bool> match, bool after)
    {
        for (var d = 0; d < text.Length; d++)
        {
            foreach (var i in (int[])[middle - d, middle + d])
            {
                if (i <= 0 || i >= text.Length - 1 || !match(text[i])) continue;
                return after ? i + 1 : i;
            }
        }
        return null;
    }

    private Task<Loaded> LoadAsync()
    {
        lock (_gate)
        {
            if (_loading is { IsFaulted: false, IsCanceled: false } loading) return loading;
            _loading = Task.Run(() =>
            {
                var files = _installer.Files;
                var started = Environment.TickCount64;
                var phonemizer = VietnamesePhonemizer.Open(files.DictionaryFile);
                try
                {
                    // Its patterns and word lists are built on the first call (about 150 ms): not with the first sentence.
                    phonemizer.Phonemize("xin chào");
                    var model = VieNeuModel.Load(files.ModelFolder, files.CodecFolder, ThreadsPerSentence);
                    _logger.LogInformation("VieNeu voice loaded in {Ms} ms", Environment.TickCount64 - started);
                    return new Loaded(model, phonemizer);
                }
                catch
                {
                    phonemizer.Dispose();
                    throw;
                }
            });
            _lastUse = Environment.TickCount64;
            _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            return _loading;
        }
    }

    private void UnloadIfIdle()
    {
        lock (_gate)
        {
            if (_users > 0) return;
            var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastUse);
            if (idle < IdleUnload)
            {
                _idle.Change(IdleUnload - idle, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        Release();
    }

    /// <summary>Lets the model go now (when no sentence is being made; otherwise when the idle timer next fires).</summary>
    public void Release()
    {
        Task<Loaded>? loading;
        lock (_gate)
        {
            if (_users > 0)
            {
                _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
                return;
            }
            loading = _loading;
            _loading = null;
        }
        if (loading is null) return;
        _ = loading.ContinueWith(t =>
        {
            t.Result.Dispose();
            _logger.LogInformation("VieNeu voice unloaded");
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _idle.Dispose();
        Release();
    }

    private sealed record Loaded(VieNeuModel Model, VietnamesePhonemizer Phonemizer) : IDisposable
    {
        public void Dispose()
        {
            Model.Dispose();
            Phonemizer.Dispose();
        }
    }
}
