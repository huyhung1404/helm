using Helm.Modules.NovelReader.Text;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader.Speech;

/// <summary>Plays a short sound by itself (a word said aloud): apart from reading aloud, with no media controls.</summary>
public interface IClipPlayer
{
    /// <summary>Plays <paramref name="audio"/> and completes when it ends; a newer clip or cancelling stops it at once.</summary>
    Task PlayAsync(SpeechAudio audio, CancellationToken ct);
}

/// <summary>An engine that can also speak a language left out of its reading-aloud voices (the phone's Chinese voice).</summary>
public interface ILanguageVoices
{
    /// <summary>A voice for <paramref name="language"/> ("zh"), or null when the engine has none.</summary>
    Task<SpeechVoice?> VoiceForAsync(string language, CancellationToken ct);
}

/// <summary>
/// A voice that says a word by itself instead of handing back sound: Xiaoxiao through the hidden Edge on the PC, which
/// reaches Microsoft's voices where a direct request is refused.
/// </summary>
public interface IWordSpeaker
{
    bool IsAvailable { get; }

    /// <summary>Says <paramref name="chinese"/>; completes when said, cancelling stops it.</summary>
    /// <exception cref="SpeechUnavailableException">The voice cannot speak now.</exception>
    Task SayAsync(string chinese, SpeechOptions options, CancellationToken ct);
}

/// <summary>
/// Says a Chinese word in standard Mandarin (a long press on a word in study mode and on Novel Lens), to learn its
/// pronunciation: Microsoft's neural voice Xiaoxiao, a little slower than speech — through the hidden Edge where there is
/// one (<see cref="IWordSpeaker"/>, the PC), else through Edge's read-aloud service — or an offline Chinese voice of this
/// device when neither is reachable. A voice that failed is passed over for a while (<see cref="OnlineRest"/>), so a press
/// never waits for it again and again. Words said are
/// kept in memory, so a word pressed again plays at once. Reading aloud pauses while a word is said and goes on
/// afterwards (<see cref="QuietReading"/>).
/// </summary>
public sealed class WordPronouncer(IEnumerable<ISpeechEngine> engines, IClipPlayer player, ILogger<WordPronouncer>? logger = null,
    TimeProvider? time = null, IWordSpeaker? speaker = null)
{
    public static readonly SpeechVoice OnlineVoice = new($"{EdgeSpeechEngine.EnginePrefix}:zh-CN-XiaoxiaoNeural", "Xiaoxiao", "zh-CN", IsOnline: true)
    {
        IsFemale = true,
    };

    /// <summary>Slower than speech, so each tone is clear.</summary>
    public static readonly SpeechOptions Options = new(0.85, 1, 1);

    /// <summary>What to do when no voice can say the word.</summary>
    public const string NoVoice = "Could not say the word: the online Chinese voice is not reachable and there is no Chinese voice on this " +
        "device. On the phone: Android Settings → Text-to-speech → Google → Install voice data → Chinese (Mandarin). On the PC: " +
        "Windows Settings → Time & language → Speech → Add voices → Chinese (Simplified).";

    /// <summary>The longest wait for the online voice before an offline one says the word.</summary>
    internal TimeSpan OnlineTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How long words go straight to the offline voice after the online one failed.</summary>
    internal static readonly TimeSpan OnlineRest = TimeSpan.FromMinutes(10);

    private const int Kept = 200;

    private readonly IReadOnlyList<ISpeechEngine> _engines = engines.ToList();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, SpeechAudio> _kept = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private DateTimeOffset _onlineRestsUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _speakerRestsUntil = DateTimeOffset.MinValue;
    private CancellationTokenSource? _current;
    private Action? _resume;

    /// <summary>
    /// Pauses reading aloud while a word is said and gives back what resumes it (null when nothing was playing); set by
    /// the reader's view model.
    /// </summary>
    public Func<Action?>? QuietReading { get; set; }

    /// <summary>
    /// Says the Chinese of <paramref name="text"/> (other characters are left out); a word pressed while another is said
    /// cuts it. Called on the UI thread. Returns why it could not be said, or null.
    /// </summary>
    public async Task<string?> SayAsync(string text)
    {
        var chinese = new string(text.Where(ChineseText.IsHan).ToArray());
        if (chinese.Length == 0) return null;
        _current?.Cancel();
        using var mine = new CancellationTokenSource();
        _current = mine;
        _resume ??= QuietReading?.Invoke();
        try
        {
            if (await SpeakerSaidAsync(chinese, mine.Token)) return null;
            var audio = await AudioOfAsync(chinese, mine.Token);
            if (audio is null) return NoVoice;
            await player.PlayAsync(audio, mine.Token);
            return null;
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not say a word");
            return "Could not say the word: " + ex.Message;
        }
        finally
        {
            // The last word said gives reading aloud back.
            if (ReferenceEquals(_current, mine))
            {
                _current = null;
                var resume = _resume;
                _resume = null;
                resume?.Invoke();
            }
        }
    }

    /// <summary>Whether the word was said by the voice that speaks by itself (the hidden Edge).</summary>
    private async Task<bool> SpeakerSaidAsync(string chinese, CancellationToken ct)
    {
        if (speaker is not { IsAvailable: true } || _time.GetUtcNow() < _speakerRestsUntil) return false;
        try
        {
            await speaker.SayAsync(chinese, Options, ct);
            return true;
        }
        catch (SpeechUnavailableException ex)
        {
            logger?.LogInformation("The hidden Edge could not say a word ({Reason}); other voices say words for a while", ex.Message);
            _speakerRestsUntil = _time.GetUtcNow() + OnlineRest;
            return false;
        }
    }

    /// <summary>The online voice's sound whenever there is one, else the offline voice's.</summary>
    private async Task<SpeechAudio?> AudioOfAsync(string chinese, CancellationToken ct)
    {
        if (_kept.TryGetValue(Key(OnlineVoice, chinese), out var kept)) return kept;
        if (_time.GetUtcNow() >= _onlineRestsUntil && await OnlineAsync(chinese, ct) is { } online)
        {
            Keep(Key(OnlineVoice, chinese), online);
            return online;
        }
        if (await OfflineVoiceAsync(ct) is not { } voice || _engines.FirstOrDefault(e => e.Prefix == voice.Engine) is not { } engine) return null;
        if (_kept.TryGetValue(Key(voice, chinese), out kept)) return kept;
        try
        {
            var audio = await engine.SynthesizeAsync(chinese, voice, Options, ct);
            Keep(Key(voice, chinese), audio);
            return audio;
        }
        catch (SpeechUnavailableException ex)
        {
            logger?.LogDebug("The offline Chinese voice could not say a word: {Reason}", ex.Message);
            return null;
        }
    }

    private async Task<SpeechAudio?> OnlineAsync(string chinese, CancellationToken ct)
    {
        if (_engines.FirstOrDefault(e => e.Prefix == OnlineVoice.Engine) is not { } edge) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OnlineTimeout);
        try
        {
            var audio = await edge.SynthesizeAsync(chinese, OnlineVoice, Options, timeout.Token);
            _onlineRestsUntil = DateTimeOffset.MinValue;
            return audio;
        }
        catch (Exception ex) when (ex is SpeechUnavailableException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger?.LogInformation("The online Chinese voice could not say a word ({Reason}); the offline one says words for a while", ex.Message);
            _onlineRestsUntil = _time.GetUtcNow() + OnlineRest;
            return null;
        }
    }

    /// <summary>An offline Chinese voice: one of the listed voices (Windows), else one an engine keeps aside (the phone's).</summary>
    private async Task<SpeechVoice?> OfflineVoiceAsync(CancellationToken ct)
    {
        var voice = _engines.SelectMany(e => e.Voices).FirstOrDefault(IsOfflineChinese);
        foreach (var other in _engines.OfType<ILanguageVoices>())
        {
            if (voice is not null) break;
            voice = await other.VoiceForAsync("zh", ct);
        }
        return voice;
    }

    private static bool IsOfflineChinese(SpeechVoice voice) =>
        !voice.IsOnline && voice.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static string Key(SpeechVoice voice, string chinese) => voice.Id + "|" + chinese;

    private void Keep(string key, SpeechAudio audio)
    {
        if (!_kept.TryAdd(key, audio)) return;
        _order.Enqueue(key);
        while (_order.Count > Kept) _kept.Remove(_order.Dequeue());
    }
}
