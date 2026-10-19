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
/// Says a Chinese word in standard Mandarin (a long press on a word in study mode and on Novel Lens), to learn its
/// pronunciation: Microsoft's neural voice Xiaoxiao through Edge's read-aloud service, a little slower than speech, or an
/// offline Chinese voice of this device when that is not reachable. Words said online are kept in memory, so a word
/// pressed again plays at once. Reading aloud pauses while a word is said and goes on afterwards (<see cref="QuietReading"/>).
/// </summary>
public sealed class WordPronouncer(IEnumerable<ISpeechEngine> engines, IClipPlayer player, ILogger<WordPronouncer>? logger = null)
{
    public static readonly SpeechVoice OnlineVoice = new($"{EdgeSpeechEngine.EnginePrefix}:zh-CN-XiaoxiaoNeural", "Xiaoxiao", "zh-CN", IsOnline: true)
    {
        IsFemale = true,
    };

    /// <summary>Slower than speech, so each tone is clear.</summary>
    public static readonly SpeechOptions Options = new(0.85, 1, 1);

    /// <summary>The longest wait for the online voice before an offline one says the word.</summary>
    internal TimeSpan OnlineTimeout { get; init; } = TimeSpan.FromSeconds(6);

    private const int Kept = 200;

    private readonly IReadOnlyList<ISpeechEngine> _engines = engines.ToList();
    private readonly Dictionary<string, SpeechAudio> _kept = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
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
            var audio = await AudioOfAsync(chinese, mine.Token);
            if (audio is null) return "Could not say the word: the Chinese voice is not reachable and this device has no offline Chinese voice.";
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

    private async Task<SpeechAudio?> AudioOfAsync(string chinese, CancellationToken ct)
    {
        if (_kept.TryGetValue(chinese, out var kept)) return kept;
        // Only the online voice's is kept: the offline one stands in until the network is back.
        if (await OnlineAsync(chinese, ct) is not { } audio) return await OfflineAsync(chinese, ct);
        Keep(chinese, audio);
        return audio;
    }

    private async Task<SpeechAudio?> OnlineAsync(string chinese, CancellationToken ct)
    {
        if (_engines.FirstOrDefault(e => e.Prefix == OnlineVoice.Engine) is not { } edge) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OnlineTimeout);
        try
        {
            return await edge.SynthesizeAsync(chinese, OnlineVoice, Options, timeout.Token);
        }
        catch (Exception ex) when (ex is SpeechUnavailableException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger?.LogDebug("The online Chinese voice could not say a word: {Reason}", ex.Message);
            return null;
        }
    }

    /// <summary>An offline Chinese voice: one of the listed voices (Windows), else one an engine keeps aside (the phone's).</summary>
    private async Task<SpeechAudio?> OfflineAsync(string chinese, CancellationToken ct)
    {
        var voice = _engines.SelectMany(e => e.Voices).FirstOrDefault(IsOfflineChinese);
        foreach (var other in _engines.OfType<ILanguageVoices>())
        {
            if (voice is not null) break;
            voice = await other.VoiceForAsync("zh", ct);
        }
        if (voice is null || _engines.FirstOrDefault(e => e.Prefix == voice.Engine) is not { } engine) return null;
        try
        {
            return await engine.SynthesizeAsync(chinese, voice, Options, ct);
        }
        catch (SpeechUnavailableException ex)
        {
            logger?.LogDebug("The offline Chinese voice could not say a word: {Reason}", ex.Message);
            return null;
        }
    }

    private static bool IsOfflineChinese(SpeechVoice voice) =>
        !voice.IsOnline && voice.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private void Keep(string chinese, SpeechAudio audio)
    {
        if (!_kept.TryAdd(chinese, audio)) return;
        _order.Enqueue(chinese);
        while (_order.Count > Kept) _kept.Remove(_order.Dequeue());
    }
}
