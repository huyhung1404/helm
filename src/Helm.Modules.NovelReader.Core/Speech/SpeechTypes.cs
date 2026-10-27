namespace Helm.Modules.NovelReader.Speech;

/// <summary>A voice of one engine. <see cref="Id"/> is "engine:voice", e.g. "vieneu:ngoc_huyen".</summary>
public sealed record SpeechVoice(string Id, string Name, string Language, bool IsOnline)
{
    public bool IsVietnamese => Language.StartsWith("vi", StringComparison.OrdinalIgnoreCase);

    /// <summary>A woman's voice, when the engine says so.</summary>
    public bool IsFemale { get; init; }

    public string Description { get; init; } = "";

    /// <summary>One of VieNeu's voices, running in Helm itself (downloaded once).</summary>
    public bool IsVieNeu => Engine == "vieneu";

    /// <summary>Speed can be set; pitch only on the system's voices (VieNeu speaks at its own pitch).</summary>
    public bool SupportsPitch => !IsVieNeu;

    /// <summary>One of the phone's own voices (Android text-to-speech, e.g. Google's Vietnamese voices).</summary>
    public bool IsOnPhone => Engine == "android";

    public string Label => IsVieNeu ? (Description.Length > 0 ? $"{Name} ({Description})" : Name) : IsOnPhone ? $"{Name} (on this phone)"
        : IsOnline ? $"{Name} (online)" : $"{Name} ({Language})";

    /// <summary>The engine's own name of the voice (after "engine:").</summary>
    public string EngineVoice => Id[(Id.IndexOf(':') + 1)..];

    public string Engine => Id[..Math.Max(0, Id.IndexOf(':'))];
}

/// <summary>How to speak: speed (0.5 to 3), pitch (0 to 2, 1 = the voice's own) and volume (0 to 1).</summary>
public sealed record SpeechOptions(double Rate, double Pitch, double Volume);

/// <summary>Synthesized speech: an MP3 or WAV file in memory.</summary>
public sealed record SpeechAudio(byte[] Data, string ContentType);

/// <summary>Text to speech: VieNeu in Helm, the system's voices, or an online service (Xiaoxiao, for words).</summary>
public interface ISpeechEngine
{
    /// <summary>The prefix of this engine's voice ids ("vieneu", "win", "android").</summary>
    string Prefix { get; }

    IReadOnlyList<SpeechVoice> Voices { get; }

    /// <summary>Reads the installed voices again (after the user added one).</summary>
    void RefreshVoices();

    /// <summary>
    /// Sentences synthesized ahead at once. More is faster only where the engine really works in parallel (a GPU); the
    /// sentence to play now never waits for these.
    /// </summary>
    int Parallel => 2;

    /// <summary>Gets ready for the first sentence (a voice in Helm loads its model); nothing for most engines.</summary>
    void WarmUp() { }

    /// <exception cref="SpeechUnavailableException">The engine cannot speak now (offline, service changed).</exception>
    Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct);
}

/// <summary>An engine could not synthesize: no network, the service refused, no such voice.</summary>
public sealed class SpeechUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A media key or headset button.</summary>
public enum MediaButton
{
    PlayPause,
    Play,
    Pause,
    Next,
    Previous,
    /// <summary>Stop reading (the ✕ on the phone's media notification).</summary>
    Stop,
}

/// <summary>Plays synthesized speech on this platform, with the system's media controls.</summary>
public interface IAudioOutput
{
    /// <summary>Plays <paramref name="audio"/> and completes when it ends; cancelling stops it at once.</summary>
    Task PlayAsync(SpeechAudio audio, CancellationToken ct);

    /// <summary>Pauses the sound playing now, in the middle of it; <see cref="Resume"/> goes on from there.</summary>
    void Pause();

    void Resume();

    /// <summary>0 to 1, on top of the voice's own volume (used to fade out for the sleep timer).</summary>
    double Volume { get; set; }

    /// <summary>What the system media controls (and the lock screen) show.</summary>
    void ShowNowPlaying(string title, string subtitle);

    void ClearNowPlaying();

    /// <summary>A media key or headset button was pressed; may come on any thread.</summary>
    event EventHandler<MediaButton>? MediaButton;
}

/// <summary>All voices of all engines, by id.</summary>
public sealed class SpeechCatalog(IEnumerable<ISpeechEngine> engines)
{
    /// <summary>VieNeu's Ngọc Huyền; until VieNeu is downloaded the first Vietnamese voice is used.</summary>
    public const string DefaultVoiceId = "vieneu:" + VieNeu.VieNeuVoices.DefaultId;

    private readonly IReadOnlyList<ISpeechEngine> _engines = engines.ToList();

    /// <summary>
    /// Vietnamese voices first: VieNeu's, the phone's own voices, then online, then the Windows voices; women first.
    /// Where the saved voice is missing (VieNeu not downloaded yet), the first one is used.
    /// </summary>
    public IReadOnlyList<SpeechVoice> Voices =>
        _engines.SelectMany(e => e.Voices)
            .OrderByDescending(v => v.IsVietnamese).ThenBy(Rank).ThenByDescending(v => v.IsFemale)
            // VieNeu's keep their own order (the editors' picks first); the sort is stable.
            .ThenBy(v => v.IsVieNeu ? "" : v.Name, StringComparer.Ordinal)
            .ToList();

    private static int Rank(SpeechVoice voice) => voice.IsVieNeu ? 0 : voice.IsOnPhone ? 1 : voice.IsOnline ? 2 : 3;

    public void Refresh()
    {
        foreach (var engine in _engines) engine.RefreshVoices();
    }

    public SpeechVoice? Find(string? id) => id is null ? null : _engines.SelectMany(e => e.Voices).FirstOrDefault(v => v.Id == id);

    public ISpeechEngine? EngineOf(SpeechVoice voice) => _engines.FirstOrDefault(e => e.Prefix == voice.Engine);

    /// <summary>A Vietnamese voice that works offline, to fall back on when the online one fails.</summary>
    public SpeechVoice? OfflineVietnamese => Voices.FirstOrDefault(v => v.IsVietnamese && !v.IsOnline);
}
