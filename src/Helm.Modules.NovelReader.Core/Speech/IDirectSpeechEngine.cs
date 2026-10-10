namespace Helm.Modules.NovelReader.Speech;

/// <summary>
/// A voice that plays by itself instead of handing back sound (HoaiMy through a hidden Microsoft Edge: Edge streams and
/// plays it). Edge only fetches a text when it starts it, so reading aloud hands it several sentences at once (one
/// pause per chunk instead of per sentence) and follows the words it reports. Such a voice cannot be downloaded ahead
/// or kept for listening offline.
/// </summary>
public interface IDirectSpeechEngine
{
    /// <summary>
    /// Speaks <paramref name="text"/> after whatever is queued already; completes when it has been spoken. Cancelling
    /// stops it and everything queued after it. <paramref name="progress"/> gets the position in the text of each word
    /// as it is spoken (it may be called on any thread).
    /// </summary>
    /// <exception cref="SpeechUnavailableException">The voice cannot speak now.</exception>
    Task SpeakAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct, Action<int>? progress = null);

    /// <summary>Pauses in the middle of a sentence.</summary>
    void Pause();

    void Resume();

    /// <summary>Stops speaking and drops everything queued.</summary>
    void Cancel();

    /// <summary>Gets ready to speak (e.g. starts Edge and loads its voices) so the first sentence starts quickly.</summary>
    Task WarmUpAsync(CancellationToken ct);
}
