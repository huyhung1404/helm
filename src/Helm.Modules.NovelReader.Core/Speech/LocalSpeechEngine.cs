using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader.Speech;

/// <summary>The local speech server (VieNeu-TTS) on this PC: started when a voice is needed, stopped when idle.</summary>
public interface ILocalVoiceServer
{
    /// <summary>Where it listens, e.g. http://127.0.0.1:8765/.</summary>
    Uri BaseAddress { get; }

    /// <summary>The server is installed on this PC (its folder exists).</summary>
    bool IsInstalled { get; }

    /// <summary>Starts the server if it is not running and waits until it answers; false when it cannot run.</summary>
    Task<bool> EnsureRunningAsync(CancellationToken ct);

    /// <summary>The voice was just used (keeps an idle timer from stopping it).</summary>
    void Touch();

    /// <summary>What the settings page shows: not installed, stopped, starting, running, or why it failed.</summary>
    string Status { get; }

    bool IsRunning { get; }

    /// <summary>Raised when <see cref="Status"/> changes, on any thread.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Stops the server if this app started it (frees the GPU memory).</summary>
    void Stop();
}

/// <summary>
/// Voices that run on this PC: VieNeu-TTS through its OpenAI-style server (<c>POST /v1/audio/speech</c>). Offline,
/// nothing leaves the PC. The server speaks at one speed, so the speed and the volume are applied here
/// (<see cref="TimeStretch"/>); pitch is not supported. The voice list is kept from the last time the server answered,
/// so it shows before the server starts.
/// </summary>
public sealed class LocalSpeechEngine : ISpeechEngine
{
    public const string EnginePrefix = "local";

    /// <summary>The rate asked from the server: 24 kHz is plenty for speech and half the size of the native 48 kHz.</summary>
    public const int SampleRate = 24000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILocalVoiceServer _server;
    private readonly ILogger<LocalSpeechEngine> _logger;
    private readonly HttpClient _http;
    private readonly string _voicesFile;
    private IReadOnlyList<SpeechVoice> _voices;

    public LocalSpeechEngine(ILocalVoiceServer server, string voicesFile, ILogger<LocalSpeechEngine> logger, HttpMessageHandler? handler = null)
    {
        _server = server;
        _logger = logger;
        _voicesFile = voicesFile;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _voices = LoadKnownVoices();
    }

    public string Prefix => EnginePrefix;

    /// <summary>The GPU synthesizes several sentences at once much faster than one after another (4060 Ti: 8 at once ≈ 9.5× listening speed).</summary>
    public int Parallel => 4;

    /// <summary>The local voices (none when VieNeu is not installed), women first.</summary>
    public IReadOnlyList<SpeechVoice> Voices => _server.IsInstalled ? _voices : [];

    /// <summary>Asks the server for its voices if it is running (it is not started just for this).</summary>
    public void RefreshVoices() => _ = RefreshVoicesAsync(CancellationToken.None);

    public async Task RefreshVoicesAsync(CancellationToken ct)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<VoiceList>(new Uri(_server.BaseAddress, "v1/voices"), Json, ct).ConfigureAwait(false);
            if (list?.Data is not { Count: > 0 } data) return;
            var voices = data.Select(ToVoice).ToList();
            _voices = Order(voices);
            Directory.CreateDirectory(Path.GetDirectoryName(_voicesFile)!);
            Text.TextFiles.WriteAllTextAtomic(_voicesFile, JsonSerializer.Serialize(data, Json));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _logger.LogDebug(ex, "Local voice list not available");
        }
    }

    public async Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct)
    {
        if (!_server.IsInstalled) throw new SpeechUnavailableException("The local voice (VieNeu-TTS) is not installed.");
        if (!await _server.EnsureRunningAsync(ct).ConfigureAwait(false))
            throw new SpeechUnavailableException("The local voice (VieNeu-TTS) could not start. See Novel Reader's settings.");
        _server.Touch();
        try
        {
            using var response = await _http.PostAsJsonAsync(new Uri(_server.BaseAddress, "v1/audio/speech"), new SpeechRequest
            {
                Input = text,
                Voice = voice.EngineVoice,
                SampleRate = SampleRate,
            }, Json, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new SpeechUnavailableException($"The local voice refused ({(int)response.StatusCode}): {Shorten(detail)}");
            }
            var rate = response.Headers.TryGetValues("X-Sample-Rate", out var values) && int.TryParse(values.FirstOrDefault(), out var r) ? r : SampleRate;
            var pcm = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (pcm.Length < 2) throw new SpeechUnavailableException("The local voice sent no sound.");
            var samples = TimeStretch.Scale(TimeStretch.Stretch(TimeStretch.FromBytes(pcm), rate, options.Rate), options.Volume);
            _server.Touch();
            return new SpeechAudio(TimeStretch.Wav(samples, rate), "audio/wav");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("The local voice is not answering: " + ex.Message, ex);
        }
    }

    private IReadOnlyList<SpeechVoice> LoadKnownVoices()
    {
        try
        {
            if (File.Exists(_voicesFile) && JsonSerializer.Deserialize<List<VoiceInfo>>(File.ReadAllText(_voicesFile), Json) is { Count: > 0 } saved)
                return Order(saved.Select(ToVoice).ToList());
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        // Before the server ever answered: the presets VieNeu v3 Turbo ships (women first).
        return Order(DefaultVoices.Select(v => new SpeechVoice($"{EnginePrefix}:{v.Name}", v.Name, "vi-VN", IsOnline: false) { IsFemale = v.Female }).ToList());
    }

    private static SpeechVoice ToVoice(VoiceInfo info) =>
        new($"{EnginePrefix}:{info.Id}", info.Name ?? info.Id, "vi-VN", IsOnline: false)
        {
            IsFemale = info.Gender?.StartsWith("f", StringComparison.OrdinalIgnoreCase) == true || info.Gender?.Contains("nữ", StringComparison.OrdinalIgnoreCase) == true,
            Description = info.Description ?? "",
        };

    private static IReadOnlyList<SpeechVoice> Order(List<SpeechVoice> voices) =>
        voices.OrderByDescending(v => v.IsFemale).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();

    private static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;

    private static readonly (string Name, bool Female)[] DefaultVoices =
    [
        ("Mai Anh", true), ("Ngọc Huyền", true), ("Trúc Ly", true), ("Thùy Dung", true), ("Ngọc Trân", true), ("Ngọc Linh", true),
        ("Đoan Trang", true), ("Quỳnh Anh", true), ("Thục Đoan", true), ("Mỹ Duyên", true), ("Kim Thanh", true),
        ("Hải Đăng", false), ("Thiện Minh", false), ("Quang Sơn", false), ("Minh Đức", false),
    ];

    private sealed class SpeechRequest
    {
        public string Model { get; init; } = "vieneu-v3-turbo";
        public string Input { get; init; } = "";
        public string Voice { get; init; } = "";
        [JsonPropertyName("response_format")] public string ResponseFormat { get; init; } = "pcm";
        [JsonPropertyName("sample_rate")] public int SampleRate { get; init; }
    }

    private sealed class VoiceList
    {
        public List<VoiceInfo>? Data { get; init; }
    }

    private sealed class VoiceInfo
    {
        public string Id { get; init; } = "";
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Gender { get; init; }
    }
}
