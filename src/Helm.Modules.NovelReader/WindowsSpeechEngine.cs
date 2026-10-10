using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The Windows voices (Settings → Time &amp; language → Speech, e.g. "Microsoft An" for Vietnamese): offline, and what
/// reading aloud falls back on when the online voice cannot be reached. Synthesizes to WAV in memory.
/// </summary>
public sealed class WindowsSpeechEngine(ILogger<WindowsSpeechEngine> logger) : ISpeechEngine, IDisposable
{
    public const string EnginePrefix = "win";

    private readonly object _gate = new();
    private IReadOnlyList<SpeechVoice>? _voices;
    private SpeechSynthesizer? _synthesizer;

    public string Prefix => EnginePrefix;

    public IReadOnlyList<SpeechVoice> Voices
    {
        get
        {
            if (_voices is null) RefreshVoices();
            return _voices!;
        }
    }

    public void RefreshVoices()
    {
        try
        {
            _voices = SpeechSynthesizer.AllVoices
                .Select(v => new SpeechVoice($"{EnginePrefix}:{v.Id}", v.DisplayName, v.Language, IsOnline: false))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list the Windows voices");
            _voices = [];
        }
    }

    public async Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct)
    {
        SpeechSynthesisStream stream;
        try
        {
            Task<SpeechSynthesisStream> synthesis;
            lock (_gate)
            {
                _synthesizer ??= new SpeechSynthesizer();
                _synthesizer.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voice.EngineVoice)
                    ?? throw new SpeechUnavailableException($"The voice {voice.Name} is no longer installed.");
                _synthesizer.Options.SpeakingRate = Math.Clamp(options.Rate, 0.5, 6);
                _synthesizer.Options.AudioPitch = Math.Clamp(options.Pitch, 0, 2);
                _synthesizer.Options.AudioVolume = Math.Clamp(options.Volume, 0, 1);
                // Started under the lock: the options belong to this request.
                synthesis = _synthesizer.SynthesizeTextToStreamAsync(text).AsTask(ct);
            }
            stream = await synthesis.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (SpeechUnavailableException or OperationCanceledException))
        {
            throw new SpeechUnavailableException("The Windows voice could not speak: " + ex.Message, ex);
        }
        using (stream)
        {
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size).AsTask(ct).ConfigureAwait(false);
            reader.ReadBytes(bytes);
            return new SpeechAudio(bytes, string.IsNullOrEmpty(stream.ContentType) ? "audio/wav" : stream.ContentType);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _synthesizer?.Dispose();
            _synthesizer = null;
        }
    }
}
