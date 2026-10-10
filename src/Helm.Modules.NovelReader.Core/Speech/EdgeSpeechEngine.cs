using System.Globalization;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader.Speech;

/// <summary>
/// Microsoft's neural voice HoaiMy (Vietnamese, female) through the read-aloud service of Microsoft Edge: free and
/// without an account, but not an official API (the same one the open-source edge-tts uses), so the version below may
/// need a bump when Microsoft changes it. One WebSocket per sentence; the text of the sentence is sent to Microsoft.
/// </summary>
public sealed class EdgeSpeechEngine(ILogger<EdgeSpeechEngine> logger) : ISpeechEngine
{
    public const string EnginePrefix = "edge";

    /// <summary>The Edge build the service expects to talk to; a too old one is refused with 403.</summary>
    internal const string ChromiumVersion = "143.0.3650.75";

    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string Endpoint = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Server time minus this PC's time, learned from a refusal, so a wrong clock does not break the token.</summary>
    private static TimeSpan _clockSkew;

    public string Prefix => EnginePrefix;

    public IReadOnlyList<SpeechVoice> Voices { get; } = [new($"{EnginePrefix}:vi-VN-HoaiMyNeural", "HoaiMy", "vi-VN", IsOnline: true)];

    public void RefreshVoices() { }

    /// <summary>
    /// The service now and then turns a request away (it closes the connection before any sound) or answers slowly. So
    /// when a request has no answer after <see cref="HedgeAfter"/>, a second one starts and the first to answer wins; a
    /// refused request is tried again at once, up to <see cref="Attempts"/> rounds. Each round waits longer for the first
    /// sound (<see cref="FirstSoundTimeouts"/>): when the service is slow, a sentence comes late instead of not at all.
    /// Reading aloud downloads ahead, so this only shows on the very first sentence.
    /// </summary>
    public async Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var firstSound = FirstSoundTimeouts[Math.Min(attempt, FirstSoundTimeouts.Count) - 1];
            var first = SynthesizeOnceAsync(text, voice, options, firstSound, round.Token);
            var pending = new List<Task<SpeechAudio>> { first };
            // The race starts later in the rounds that wait longer.
            var hedge = Task.Delay(HedgeAfter * (firstSound / FirstSoundTimeouts[0]), round.Token);
            // Race a second request only while a connection is free: the service slows down an address that opens many.
            if (await Task.WhenAny(first, hedge).ConfigureAwait(false) == hedge && !first.IsCompleted && Connections.CurrentCount > 0)
                pending.Add(SynthesizeOnceAsync(text, voice, options, firstSound, round.Token));
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                if (done.IsCompletedSuccessfully)
                {
                    round.Cancel();
                    return done.Result;
                }
                ct.ThrowIfCancellationRequested();
                last = done.Exception?.GetBaseException() ?? last;
            }
            logger.LogDebug("Online voice attempt {Attempt} failed: {Reason}", attempt, last?.Message);
            await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct).ConfigureAwait(false);
        }
        throw new SpeechUnavailableException("The online voice is not reachable: " + Describe(last), last);
    }

    /// <summary>
    /// Why a request failed, in words. Our own reasons are kept; the system's are not shown as they are (a phone's
    /// trimmed runtime gives resource keys such as "net_WebSockets_ConnectionClosedPrematurely_Generic").
    /// </summary>
    internal static string Describe(Exception? error) => error switch
    {
        TimeoutException => "it did not answer in time.",
        WebSocketException web when web.Message.Contains("'403'", StringComparison.Ordinal) => "the service refused the request.",
        WebSocketException => "the connection was cut before any sound.",
        HttpRequestException or System.Net.Sockets.SocketException => "there is no connection to the service.",
        IOException => "it sent no sound.",
        _ => "the service did not answer.",
    };

    /// <summary>Rounds of requests per sentence before giving up (and falling back to an offline voice).</summary>
    internal const int Attempts = 4;

    /// <summary>How long a request may take before a second one races it.</summary>
    internal static TimeSpan HedgeAfter { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Connections to the service at once, from all of Novel Reader.</summary>
    private static readonly SemaphoreSlim Connections = new(3, 3);

    /// <summary>
    /// A request with no sound this long after it started is given up and tried again (the service sometimes hangs), a
    /// little longer each round (it is sometimes just slow).
    /// </summary>
    internal static IReadOnlyList<TimeSpan> FirstSoundTimeouts { get; set; } =
        [TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)];

    private async Task<SpeechAudio> SynthesizeOnceAsync(string text, SpeechVoice voice, SpeechOptions options, TimeSpan firstSound, CancellationToken ct)
    {
        await Connections.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RequestAsync(text, voice, options, firstSound, ct).ConfigureAwait(false);
        }
        finally
        {
            Connections.Release();
        }
    }

    private async Task<SpeechAudio> RequestAsync(string text, SpeechVoice voice, SpeechOptions options, TimeSpan firstSound, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(firstSound);
        var token = timeout.Token;
        var requestId = Guid.NewGuid().ToString("N");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        socket.Options.SetRequestHeader("Pragma", "no-cache");
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("Cookie", $"muid={Convert.ToHexString(RandomNumberGenerator.GetBytes(16))};");
        var url = $"{Endpoint}?TrustedClientToken={TrustedClientToken}&Sec-MS-GEC={SecMsGec(DateTimeOffset.UtcNow + _clockSkew)}" +
                  $"&Sec-MS-GEC-Version=1-{ChromiumVersion}&ConnectionId={requestId}";
        try
        {
            await socket.ConnectAsync(new Uri(url), token).ConfigureAwait(false);
        }
        catch (WebSocketException ex) when (ex.Message.Contains("'403'", StringComparison.Ordinal))
        {
            // The token is tied to the time: a wrong clock on this PC is refused. Learn the server clock for the retry.
            await LearnClockAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The online voice did not answer in time.");
        }

        await SendAsync(socket, $"X-Timestamp:{Timestamp()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}", token).ConfigureAwait(false);
        await SendAsync(socket, $"X-RequestId:{requestId}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{Timestamp()}Z\r\nPath:ssml\r\n\r\n" +
            Ssml(text, voice.EngineVoice, options), token).ConfigureAwait(false);

        using var audio = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("The online voice closed the connection.");
                var data = message.GetBuffer().AsSpan(0, (int)message.Length);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (Encoding.UTF8.GetString(data).Contains("Path:turn.end", StringComparison.Ordinal)) break;
                    continue;
                }
                // Binary: a two-byte header length, the headers, then the audio.
                if (data.Length < 2) continue;
                var headerLength = (data[0] << 8) | data[1];
                if (2 + headerLength > data.Length) continue;
                if (Encoding.UTF8.GetString(data.Slice(2, headerLength)).Contains("Path:audio", StringComparison.Ordinal))
                {
                    // Sound is coming: the rest may take longer than the first answer.
                    if (audio.Length == 0) timeout.CancelAfter(Timeout);
                    audio.Write(data[(2 + headerLength)..]);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The online voice did not answer in time.");
        }
        if (audio.Length == 0) throw new IOException("The online voice sent no sound.");
        return new SpeechAudio(audio.ToArray(), "audio/mpeg");
    }

    private static readonly HttpClient Clock = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task LearnClockAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://speech.platform.bing.com/");
            using var response = await Clock.SendAsync(request).ConfigureAwait(false);
            if (response.Headers.Date is { } server) _clockSkew = server - DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    private static string UserAgent
    {
        get
        {
            var major = ChromiumVersion[..ChromiumVersion.IndexOf('.')];
            return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0";
        }
    }

    /// <summary>
    /// The Sec-MS-GEC token: SHA-256 of the Windows file time rounded down to 5 minutes and the client token, upper-case
    /// hex (as Edge computes it).
    /// </summary>
    internal static string SecMsGec(DateTimeOffset now)
    {
        var ticks = now.UtcDateTime.ToFileTimeUtc();
        ticks -= ticks % 3_000_000_000L;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticks.ToString(CultureInfo.InvariantCulture) + TrustedClientToken)));
    }

    /// <summary>The request: the voice, speed, pitch and volume as SSML prosody, the text escaped.</summary>
    internal static string Ssml(string text, string voice, SpeechOptions options)
    {
        var rate = (int)Math.Round((Math.Clamp(options.Rate, 0.5, 3) - 1) * 100);
        var pitch = (int)Math.Round((Math.Clamp(options.Pitch, 0, 2) - 1) * 50);
        var volume = (int)Math.Round((Math.Clamp(options.Volume, 0, 1) - 1) * 100);
        var clean = new string(text.Where(c => c is '\t' or '\n' or '\r' || (c >= ' ' && !char.IsSurrogate(c))).ToArray());
        return "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='vi-VN'>" +
               $"<voice name='{voice}'><prosody pitch='{pitch:+0;-0}Hz' rate='{rate:+0;-0}%' volume='{volume:+0;-0}%'>" +
               SecurityElement.Escape(clean) + "</prosody></voice></speak>";
    }

    private static string Timestamp() =>
        DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", CultureInfo.InvariantCulture);

    private static Task SendAsync(ClientWebSocket socket, string message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, ct);
}
