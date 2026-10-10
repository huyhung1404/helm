using System.Collections.Concurrent;
using Android.OS;
using Android.Speech.Tts;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using Locale = Java.Util.Locale;

namespace Helm.Modules.NovelReader.Playback;

/// <summary>
/// The phone's own Vietnamese voices (Android text-to-speech: Google's on most phones, offline once its voice data is
/// installed). Each sentence is synthesized to a WAV file and handed back as sound, so reading aloud downloads ahead
/// and keeps sound exactly as with the online voice. Voice ids are "android:&lt;voice name&gt;", e.g.
/// "android:vi-vn-x-gft-local". The engine starts when Novel Reader is opened (<see cref="EnsureStarted"/>), not with
/// Helm; <see cref="VoicesChanged"/> says when its voices are known.
/// </summary>
public sealed class AndroidTtsEngine : ISpeechEngine
{
    public const string EnginePrefix = "android";

    /// <summary>A sentence of a novel takes a fraction of a second; a stuck engine gives up after this.</summary>
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(30);

    private static readonly Locale Vietnamese = Locale.ForLanguageTag("vi-VN")!;

    private readonly ILogger<AndroidTtsEngine> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.Ordinal);
    private readonly string _folder;
    private const string GoogleEngine = "com.google.android.tts";

    private TextToSpeech? _tts;
    private bool _starting;
    private bool _initFailed;

    /// <summary>The engine named explicitly after the default one failed (null: the phone's default).</summary>
    private string? _engine;
    private TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dictionary<string, Voice> _native = new(StringComparer.Ordinal);
    private IReadOnlyList<SpeechVoice> _voices = [];

    public AndroidTtsEngine(ILogger<AndroidTtsEngine> logger)
    {
        _logger = logger;
        _folder = Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "novel-reader-tts");
        Directory.CreateDirectory(_folder);
    }

    public string Prefix => EnginePrefix;

    public IReadOnlyList<SpeechVoice> Voices => _voices;

    /// <summary>The engine started (or failed to): its voices are known now. May come on any thread.</summary>
    public event EventHandler? VoicesChanged;

    /// <summary>The phone has a Vietnamese voice installed.</summary>
    public bool HasVietnamese => _voices.Count > 0;

    /// <summary>Starts the phone's text-to-speech if it is not running (it reports to the main thread).</summary>
    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_tts is not null || _starting) return;
            _starting = true;
        }
        new Handler(Looper.MainLooper!).Post(Start);
    }

    public void RefreshVoices()
    {
        lock (_gate)
        {
            if (_tts is not null && _ready.Task.IsCompletedSuccessfully && _ready.Task.Result) LoadVoices();
        }
    }

    public async Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct)
    {
        EnsureStarted();
        bool ready;
        try
        {
            ready = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ready = false;
        }
        if (!ready) throw new SpeechUnavailableException("The phone's text-to-speech did not start. Check it in Android's settings (Text-to-speech output).");

        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_folder, id + ".wav");
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = done;
        try
        {
            OperationResult result;
            lock (_gate)
            {
                if (_tts is not { } tts || !_native.TryGetValue(voice.EngineVoice, out var native))
                    throw new SpeechUnavailableException($"The voice {voice.Name} is not on this phone any more.");
                // Voice, speed and pitch are taken when the request is queued, so requests in a row do not mix them.
                tts.SetVoice(native);
                tts.SetSpeechRate((float)Math.Clamp(options.Rate, 0.5, 3));
                tts.SetPitch((float)Math.Clamp(options.Pitch, 0.5, 2));
                result = tts.SynthesizeToFile(text, new Bundle(), new Java.IO.File(path), id);
            }
            if (result != OperationResult.Success)
            {
                Restart();
                throw new SpeechUnavailableException("The phone's text-to-speech did not accept the sentence.");
            }
            bool ok;
            try
            {
                ok = await done.Task.WaitAsync(SynthesisTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ok = false;
            }
            if (!ok || !File.Exists(path)) throw new SpeechUnavailableException("The phone's text-to-speech could not read the sentence.");
            var data = await File.ReadAllBytesAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (options.Volume < 0.999) data = ScaleWav(data, options.Volume);
            return new SpeechAudio(data, "audio/wav");
        }
        finally
        {
            _pending.TryRemove(id, out _);
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Reads a sentence straight away with a voice (to choose one in the settings).</summary>
    public void Preview(SpeechVoice voice, string text)
    {
        lock (_gate)
        {
            if (_tts is not { } tts || !_native.TryGetValue(voice.EngineVoice, out var native)) return;
            tts.SetVoice(native);
            tts.SetSpeechRate(1);
            tts.SetPitch(1);
            tts.Speak(text, QueueMode.Flush, null, "preview");
        }
    }

    private void Start()
    {
        lock (_gate)
        {
            _starting = false;
            if (_tts is not null) return;
            _initFailed = false;
            _logger.LogInformation("Starting the phone's text-to-speech ({Engine})", _engine ?? "the default engine");
            TextToSpeech created;
            try
            {
                created = _engine is null
                    ? new TextToSpeech(AndroidApp.Context, new InitListener(this))
                    : new TextToSpeech(AndroidApp.Context, new InitListener(this), _engine);
            }
            catch (Java.Lang.Exception ex)
            {
                _logger.LogWarning(ex, "The phone's text-to-speech could not start");
                _ready.TrySetResult(false);
                return;
            }
            // A failure is reported from inside the constructor: that instance is of no use.
            if (_initFailed) Shut(created);
            else _tts = created;
        }
    }

    private static void Shut(TextToSpeech tts)
    {
        try
        {
            tts.Shutdown();
        }
        catch (Java.Lang.Exception)
        {
        }
    }

    /// <summary>
    /// An engine to name explicitly when the default one does not start (a phone with no preferred engine set, which
    /// Android then treats as none): Google's if it is installed, else the first one.
    /// </summary>
    private string? OtherEngine()
    {
        try
        {
            var found = AndroidApp.Context.PackageManager?.QueryIntentServices(new Android.Content.Intent("android.intent.action.TTS_SERVICE"), 0);
            var packages = found?.Select(r => r.ServiceInfo?.PackageName).OfType<string>().Distinct().ToList() ?? [];
            _logger.LogInformation("Text-to-speech engines on the phone: {Engines}", packages.Count == 0 ? "none" : string.Join(", ", packages));
            return packages.Contains(GoogleEngine) ? GoogleEngine : packages.FirstOrDefault();
        }
        catch (Java.Lang.Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the phone's text-to-speech engines");
            return null;
        }
    }

    /// <summary>The service died (another engine was picked, an update): start it again for the next sentence.</summary>
    private void Restart()
    {
        lock (_gate)
        {
            try
            {
                _tts?.Shutdown();
            }
            catch (Java.Lang.Exception)
            {
            }
            _tts = null;
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        EnsureStarted();
    }

    private void OnInit(OperationResult status)
    {
        var retry = false;
        lock (_gate)
        {
            if (status != OperationResult.Success)
            {
                _initFailed = true;
                if (_tts is { } failed)
                {
                    Shut(failed);
                    _tts = null;
                }
                if (_engine is null && OtherEngine() is { } engine)
                {
                    _logger.LogInformation("The default text-to-speech engine did not start; trying {Engine}", engine);
                    _engine = engine;
                    retry = true;
                }
                else
                {
                    _logger.LogWarning("The phone's text-to-speech did not start ({Status})", status);
                    _ready.TrySetResult(false);
                    return;
                }
            }
            else if (_tts is { } tts)
            {
                tts.SetOnUtteranceProgressListener(new ProgressListener(this));
                LoadVoices();
                _ready.TrySetResult(true);
            }
            else
            {
                return;
            }
        }
        if (retry) EnsureStarted();
        else VoicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The installed Vietnamese voices; the phone's default Vietnamese voice is "Vietnamese 1".</summary>
    private void LoadVoices()
    {
        if (_tts is not { } tts) return;
        var all = new List<Voice>();
        try
        {
            foreach (var voice in tts.Voices ?? [])
            {
                if (voice.Locale?.Language != "vi") continue;
                // Listed but its data is not on the phone yet.
                if (voice.Features?.Contains(TextToSpeech.Engine.KeyFeatureNotInstalled) == true) continue;
                all.Add(voice);
            }
        }
        catch (Java.Lang.Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the phone's voices");
        }
        string? preferred = null;
        try
        {
            if (tts.IsLanguageAvailable(Vietnamese) >= LanguageAvailableResult.Available)
            {
                tts.SetLanguage(Vietnamese);
                preferred = tts.Voice?.Name;
            }
        }
        catch (Java.Lang.Exception)
        {
        }
        // "vi-VN-language" only stands for the default voice; a voice offered both offline and over the network (Google
        // lists "…-gft-local" and "…-gft-network") is kept once, offline.
        var named = all.Where(v => v.Name is { } n && !n.EndsWith("-language", StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count > 0) all = named;
        var offline = all.Where(v => !v.IsNetworkConnectionRequired).Select(v => Code(v.Name!)).ToHashSet(StringComparer.Ordinal);
        all = all.Where(v => !v.IsNetworkConnectionRequired || !offline.Contains(Code(v.Name!))).ToList();
        if (preferred is not null && all.All(v => v.Name != preferred))
            preferred = all.FirstOrDefault(v => !v.IsNetworkConnectionRequired && Code(v.Name!) == "gft")?.Name
                ?? all.FirstOrDefault(v => !v.IsNetworkConnectionRequired)?.Name;
        // Offline voices before the ones that need the network; the default one first.
        var ordered = all
            .OrderByDescending(v => v.Name == preferred)
            .ThenBy(v => v.IsNetworkConnectionRequired)
            .ThenBy(v => v.Name, StringComparer.Ordinal)
            .ToList();
        _logger.LogInformation("The phone's text-to-speech is ready: {Count} Vietnamese voices ({Names}), default {Preferred}",
            ordered.Count, string.Join(", ", ordered.Select(v => v.Name)), preferred ?? "none");
        _native = ordered.ToDictionary(v => v.Name!, StringComparer.Ordinal);
        _voices = ordered.Select((v, i) => new SpeechVoice($"{EnginePrefix}:{v.Name}", $"Vietnamese {i + 1}", "vi-VN", v.IsNetworkConnectionRequired)
        {
            Description = (v.Name == preferred ? "The phone's default Vietnamese voice" : "A voice of the phone") + $" ({Code(v.Name!)})"
                + (v.IsNetworkConnectionRequired ? ", needs the network" : ", works offline"),
        }).ToList();
    }

    /// <summary>"vi-vn-x-gft-local" → "gft".</summary>
    private static string Code(string name)
    {
        var parts = name.Split('-');
        return parts.Length >= 4 && parts[2] == "x" ? parts[3] : name;
    }

    /// <summary>A quieter copy of a 16-bit PCM WAV (the volume setting); anything else is returned as it is.</summary>
    internal static byte[] ScaleWav(byte[] wav, double volume)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' || BitConverter.ToInt16(wav, 34) != 16) return wav;
        var copy = (byte[])wav.Clone();
        for (var i = 44; i + 1 < copy.Length; i += 2)
        {
            var sample = (short)Math.Clamp(BitConverter.ToInt16(copy, i) * volume, short.MinValue, short.MaxValue);
            copy[i] = (byte)sample;
            copy[i + 1] = (byte)(sample >> 8);
        }
        return copy;
    }

    private void Complete(string? id, bool ok)
    {
        if (id is not null && _pending.TryGetValue(id, out var pending)) pending.TrySetResult(ok);
    }

    private sealed class InitListener(AndroidTtsEngine engine) : Java.Lang.Object, TextToSpeech.IOnInitListener
    {
        public void OnInit(OperationResult status) => engine.OnInit(status);
    }

    private sealed class ProgressListener(AndroidTtsEngine engine) : UtteranceProgressListener
    {
        public override void OnStart(string? utteranceId)
        {
        }

        public override void OnDone(string? utteranceId) => engine.Complete(utteranceId, true);

        [Obsolete("Android calls the overload with an error code on API 21+; this one is still abstract.")]
        public override void OnError(string? utteranceId) => engine.Complete(utteranceId, false);

        public override void OnError(string? utteranceId, TextToSpeechError errorCode) => engine.Complete(utteranceId, false);

        public override void OnStop(string? utteranceId, bool interrupted) => engine.Complete(utteranceId, false);
    }
}
