using System.Globalization;
using System.Text.Json;
using System.Windows;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Helm.Modules.WatchLater.Player;

/// <summary>What the player shows: a YouTube or Facebook embed, or a downloaded file.</summary>
/// <param name="Page">The player page with its query (https://helm-player.local/player.html?…).</param>
/// <param name="FileFolder">For a downloaded file: its folder, served as https://helm-file.local/.</param>
internal sealed record PlayerSource(string Page, string? FileFolder);

/// <summary>
/// One video playing in WebView2 (Windows), on the Watch Later page or in the mini player. It saves where watching is
/// (<see cref="PlaybackTracker"/>), marks the video watched near its end, and plays at the chosen speed: the speed is
/// set on the page's &lt;video&gt; elements directly, in every frame, because YouTube's own player stops at 2×.
/// </summary>
public partial class PlayerView
{
    internal const string PlayerHost = "helm-player.local";
    internal const string FileHost = "helm-file.local";

    /// <summary>The speeds offered.</summary>
    public static IReadOnlyList<double> Speeds { get; } = [1, 1.5, 2, 2.5, 3];

    /// <summary>
    /// HELM_PLAYER_TEST=1 (dev and test instances only): the player is muted, and the mini player opens without the
    /// focus and off screen, so checking it does not disturb the desktop it runs on.
    /// </summary>
    internal static bool TestMode { get; } = Environment.GetEnvironmentVariable("HELM_PLAYER_TEST") == "1";

    private WatchLaterStore? _store;
    private IProcessLauncher? _launcher;
    private ILogger? _logger;
    private Func<Task<CoreWebView2Environment>>? _environment;
    private string _playerFolder = "";
    private readonly List<CoreWebView2Frame> _frames = [];
    private PlaybackTracker? _tracker;
    private Task? _init;
    private bool _ready;
    private PlayerSource? _pending;
    private double _rate = 1;

    public PlayerView() => InitializeComponent();

    /// <summary>The video playing (or last played); null when nothing is.</summary>
    public string? CurrentId { get; private set; }

    /// <summary>A line about the playback for the host to show ("Carrying on from 3:12", "Marked as watched").</summary>
    public event EventHandler<string>? StatusChanged;

    internal void Attach(WatchLaterStore store, IProcessLauncher launcher, ILogger logger, Func<Task<CoreWebView2Environment>> environment, string playerFolder)
    {
        _store = store;
        _launcher = launcher;
        _logger = logger;
        _environment = environment;
        _playerFolder = playerFolder;
    }

    /// <summary>Plays a video at <paramref name="rate"/>; the one playing before is saved where it was.</summary>
    internal void Play(string id, PlayerSource source, double rate)
    {
        if (_store is null) return;
        _tracker?.Flush();
        CurrentId = id;
        _rate = rate;
        var item = _store.Get(id);
        var where = source.FileFolder is not null ? "Downloaded file · plays offline" : SourceText(item);
        Status(item?.ResumeSeconds is int at && at > 0 ? $"{where} · carrying on from {WatchLaterFormat.Duration(at)}" : where);
        _tracker = new PlaybackTracker(_store, id);
        _tracker.MarkedWatched += (_, _) => Dispatcher.BeginInvoke(() => Status("Marked as watched"));
        ShowProblem(null);
        if (!_ready)
        {
            _pending = source;
            _init ??= InitializeAsync();
            return;
        }
        Navigate(source);
    }

    /// <summary>Stops: where it was is saved and the page is emptied (no sound keeps playing).</summary>
    public void Stop()
    {
        _tracker?.Flush();
        _tracker = null;
        CurrentId = null;
        _pending = null;
        try
        {
            if (_ready) Web.CoreWebView2.Navigate("about:blank");
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Pauses (leaving the page): where it was is saved.</summary>
    public void Pause()
    {
        _tracker?.Flush();
        Run("window.helmPause && window.helmPause();", frames: false);
    }

    /// <summary>Saves where it is now (before the video moves to the mini player or back).</summary>
    public void Flush() => _tracker?.Flush();

    /// <summary>Plays at this speed from now on (1×, 1.5×, 2×, 2.5×, 3×).</summary>
    public void SetRate(double rate)
    {
        _rate = rate;
        Run(RateScript(rate), frames: true);
    }

    /// <summary>Marks the video playing as watched.</summary>
    public void MarkWatched()
    {
        if (_store is null || CurrentId is not { } id) return;
        _tracker?.Flush();
        _store.SetWatched(id, true);
        Status("Marked as watched");
    }

    /// <summary>Opens the video playing in the browser, where it stopped.</summary>
    public void OpenInBrowser()
    {
        if (_store is null || _launcher is null || CurrentId is not { } id || _store.Get(id) is not { } item) return;
        Pause();
        _launcher.OpenUrl(WatchLaterFormat.ResumeUrl(_store.Get(id) ?? item));
    }

    private static string SourceText(WatchItem? item) =>
        item is null ? "" : string.Join(" · ", new[] { item.Channel, WatchLaterFormat.KindName(item.Source, item.Kind) }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private void Status(string text) => StatusChanged?.Invoke(this, text);

    private async Task InitializeAsync()
    {
        try
        {
            var env = await _environment!().ConfigureAwait(true);
            await Web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // Test instances play silently, so checking the player makes no noise.
            core.IsMuted = TestMode;
            core.SetVirtualHostNameToFolderMapping(PlayerHost, _playerFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnMessage;
            core.FrameCreated += (_, e) => Track(e.Frame);
            core.NavigationStarting += (_, _) => _frames.Clear();
            core.DOMContentLoaded += (_, _) => Run(RateScript(_rate), frames: false);
            // Links inside the player (the channel, "Watch on YouTube") open in the browser, not in a new window.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") _launcher?.OpenUrl(uri.AbsoluteUri);
            };
            _ready = true;
            if (_pending is { } pending)
            {
                _pending = null;
                Navigate(pending);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The player could not start WebView2");
            ShowProblem($"The player could not start ({ex.Message}). Open the video in the browser instead.");
        }
    }

    /// <summary>A frame of the page (YouTube's or Facebook's player, and frames inside them) gets the speed too.</summary>
    private void Track(CoreWebView2Frame frame)
    {
        _frames.Add(frame);
        frame.Destroyed += (_, _) => _frames.Remove(frame);
        frame.DOMContentLoaded += (_, _) => Execute(frame, RateScript(_rate));
        frame.FrameCreated += (_, e) => Track(e.Frame);
    }

    private void Run(string script, bool frames)
    {
        if (!_ready) return;
        try
        {
            _ = Web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException) { }
        if (!frames) return;
        foreach (var frame in _frames.ToList()) Execute(frame, script);
    }

    private void Execute(CoreWebView2Frame frame, string script)
    {
        try
        {
            _ = frame.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The frame went away.
        }
    }

    /// <summary>
    /// Sets every &lt;video&gt; of a document to the speed, and keeps it there: the site's player may put its own speed
    /// back when a video starts, seeks or changes quality.
    /// </summary>
    internal static string RateScript(double rate)
    {
        var r = rate.ToString("0.##", CultureInfo.InvariantCulture);
        return "(function(r){window.__helmRate=r;function a(){document.querySelectorAll('video').forEach(function(v){if(Math.abs(v.playbackRate-window.__helmRate)>0.01){try{v.playbackRate=window.__helmRate;}catch(e){}}});}"
               + "a();if(!window.__helmRateTimer){window.__helmRateTimer=setInterval(a,500);}})(" + r + ");";
    }

    private void Navigate(PlayerSource source)
    {
        try
        {
            var core = Web.CoreWebView2;
            core.ClearVirtualHostNameToFolderMapping(FileHost);
            if (source.FileFolder is { } folder) core.SetVirtualHostNameToFolderMapping(FileHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate(source.Page);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger?.LogWarning(ex, "The player could not open the video");
            ShowProblem($"The video could not be opened here ({ex.Message}).");
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "progress":
                    _tracker?.Report(root.GetProperty("t").GetDouble(), root.GetProperty("d").GetDouble());
                    if (!root.GetProperty("playing").GetBoolean()) _tracker?.Flush();
                    break;
                case "ended":
                    if (CurrentId is { } id && _store?.Get(id) is { DurationSeconds: int length } && length > 0) _tracker?.Report(length, length);
                    _tracker?.Flush();
                    if (CurrentId is { } done) _store?.SetWatched(done, true);
                    Status("Watched");
                    break;
                case "error":
                    var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                    ShowProblem(ErrorText(code, message));
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _logger?.LogDebug(ex, "Unexpected message from the player page");
        }
    }

    /// <summary>YouTube's player error codes, in words.</summary>
    internal static string ErrorText(int code, string? message) => code switch
    {
        101 or 150 or 152 => "The owner of this video does not let it play outside YouTube.",
        100 => "This video was removed or is private.",
        153 => "YouTube refused to play the video here.",
        2 => "YouTube did not accept the video link.",
        5 => "This video cannot be played in the player.",
        _ => string.IsNullOrWhiteSpace(message) ? "This video cannot be played here." : message,
    };

    private void ShowProblem(string? text)
    {
        Problem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Web.Visibility = text is null ? Visibility.Visible : Visibility.Hidden;
        ProblemText.Text = text ?? "";
        if (text is null) return;
        try
        {
            if (_ready) Web.CoreWebView2.Navigate("about:blank");
        }
        catch (InvalidOperationException) { }
    }

    private void Browser_Click(object sender, RoutedEventArgs e) => OpenInBrowser();

    /// <summary>The page for a video: a query the player page reads.</summary>
    internal static string PageFor(string kind, string key, string value, int start) =>
        $"https://{PlayerHost}/player.html?kind={kind}&{key}={Uri.EscapeDataString(value)}&start={start.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Frees WebView2 (the mini player closing).</summary>
    public void Release()
    {
        Stop();
        try
        {
            Web.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger?.LogDebug(ex, "Closing WebView2");
        }
    }
}
