using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Helm.Modules.WatchLater.Player;

/// <summary>What the player shows: a YouTube or Facebook embed, or a downloaded file.</summary>
/// <param name="Page">The player page with its query (https://helm-player.local/player.html?…).</param>
/// <param name="FileFolder">For a downloaded file: its folder, served as https://helm-file.local/.</param>
internal sealed record PlayerSource(string Page, string? FileFolder);

/// <summary>
/// The Watch Later player window (Windows). It plays one video at a time in WebView2, saves where watching is
/// (<see cref="PlaybackTracker"/>) and marks the video watched near its end. The mini player is the same window,
/// small and always on top, with one thin bar to drag it by. Positions of both are remembered.
/// </summary>
public partial class PlayerWindow
{
    internal const string PlayerHost = "helm-player.local";
    internal const string FileHost = "helm-file.local";

    /// <summary>
    /// HELM_PLAYER_TEST=1 (dev and test instances only): the player is muted, opens without taking the focus and off
    /// screen, so checking it does not disturb the desktop it runs on.
    /// </summary>
    internal static bool TestMode { get; } = System.Environment.GetEnvironmentVariable("HELM_PLAYER_TEST") == "1";

    private readonly WatchLaterStore _store;
    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger _logger;
    private readonly Func<Task<CoreWebView2Environment>> _environment;
    private readonly string _playerFolder;
    private PlaybackTracker? _tracker;
    private string? _id;
    private bool _mini;
    private bool _ready;
    private (string Id, PlayerSource Source)? _pending;

    internal PlayerWindow(WatchLaterStore store, ISettingsStore<WatchLaterSettings> settings, IProcessLauncher launcher, ILogger logger,
        Func<Task<CoreWebView2Environment>> environment, string playerFolder)
    {
        _store = store;
        _settings = settings;
        _launcher = launcher;
        _logger = logger;
        _environment = environment;
        _playerFolder = playerFolder;
        InitializeComponent();
        ApplyMode(settings.Current.PlayerMini, first: true);
        if (TestMode)
        {
            ShowActivated = false;
            Left = -4000;
        }
        Loaded += async (_, _) => await InitializeAsync().ConfigureAwait(true);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _mini) SetMini(false);
        };
    }

    /// <summary>The video playing now (or last).</summary>
    public string? CurrentId => _id;

    /// <summary>Plays a video; the one playing before is saved where it was.</summary>
    internal void Play(string id, PlayerSource source)
    {
        _tracker?.Flush();
        _id = id;
        var item = _store.Get(id);
        var title = item?.DisplayTitle ?? "Watch Later";
        Title = title;
        TitleBar.Title = title;
        VideoTitle.Text = title;
        MiniTitle.Text = title;
        var where = source.FileFolder is not null ? "Downloaded file · plays offline" : SourceText(item);
        Status.Text = item?.ResumeSeconds is int at && at > 0 ? $"{where} · carrying on from {WatchLaterFormat.Duration(at)}" : where;
        _tracker = new PlaybackTracker(_store, id);
        _tracker.MarkedWatched += (_, _) => Dispatcher.BeginInvoke(() => Status.Text = "Marked as watched");
        ShowProblem(null);
        if (!_ready)
        {
            _pending = (id, source);
            return;
        }
        Navigate(source);
    }

    private static string SourceText(WatchItem? item) =>
        item is null ? "" : string.Join(" · ", new[] { item.Channel, WatchLaterFormat.KindName(item.Source, item.Kind) }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private async Task InitializeAsync()
    {
        try
        {
            var env = await _environment().ConfigureAwait(true);
            await Web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            // Test instances play silently, so checking the player makes no noise.
            core.IsMuted = TestMode;
            core.SetVirtualHostNameToFolderMapping(PlayerHost, _playerFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnMessage;
            // Links inside the player (the channel, "Watch on YouTube") open in the browser, not in a new Helm window.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") _launcher.OpenUrl(uri.AbsoluteUri);
            };
            _ready = true;
            if (_pending is { } pending)
            {
                _pending = null;
                if (pending.Id == _id) Navigate(pending.Source);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The player could not start WebView2");
            ShowProblem($"The player could not start ({ex.Message}). Open the video in the browser instead.");
        }
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
            _logger.LogWarning(ex, "The player could not open the video");
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
                    if (_id is { } id && _store.Get(id) is { } item && item.DurationSeconds is int length && length > 0) _tracker?.Report(length, length);
                    _tracker?.Flush();
                    if (_id is { } done) _store.SetWatched(done, true);
                    Status.Text = "Watched";
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
            _logger.LogDebug(ex, "Unexpected message from the player page");
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
        if (text is not null)
        {
            try
            {
                if (_ready) Web.CoreWebView2.Navigate("about:blank");
            }
            catch (InvalidOperationException) { }
        }
    }

    // ---- Buttons -------------------------------------------------------------------------------------------------

    private void Watched_Click(object sender, RoutedEventArgs e)
    {
        if (_id is not { } id) return;
        _tracker?.Flush();
        _store.SetWatched(id, true);
        Status.Text = "Marked as watched";
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (_id is not { } id || _store.Get(id) is not { } item) return;
        _tracker?.Flush();
        _launcher.OpenUrl(WatchLaterFormat.ResumeUrl(_store.Get(id) ?? item));
    }

    private void Mini_Click(object sender, RoutedEventArgs e) => SetMini(!_mini);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MiniBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetMini(false);
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException) { }
    }

    // ---- Normal and mini -----------------------------------------------------------------------------------------

    private void SetMini(bool mini)
    {
        if (mini == _mini) return;
        SaveBounds();
        ApplyMode(mini, first: false);
        _settings.Update(s => s.PlayerMini = mini);
    }

    private void ApplyMode(bool mini, bool first)
    {
        _mini = mini;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Topmost = mini;
        TitleBar.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MiniBar.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        var saved = mini ? _settings.Current.MiniBounds : _settings.Current.PlayerBounds;
        if (saved is { Length: 4 } b && IsOnScreen(b))
        {
            Left = b[0];
            Top = b[1];
            Width = b[2];
            Height = b[3];
        }
        else if (mini)
        {
            // Bottom-right of the screen, 16:9 video under the bar.
            var area = SystemParameters.WorkArea;
            Width = 432;
            Height = 432 * 9 / 16.0 + 32;
            Left = area.Right - Width - 16;
            Top = area.Bottom - Height - 16;
        }
        else if (first || Width < 480)
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(1120, area.Width * 0.8);
            Height = Math.Min(720, area.Height * 0.8);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
        if (TestMode)
        {
            // Never on the desktop of whoever runs the test, not even the mini player.
            Topmost = false;
            Left = -4000;
        }
    }

    private static bool IsOnScreen(double[] b) =>
        b[2] >= 200 && b[3] >= 120
        && b[0] + b[2] > SystemParameters.VirtualScreenLeft + 40 && b[0] < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
        && b[1] >= SystemParameters.VirtualScreenTop - 8 && b[1] < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    private void SaveBounds()
    {
        if (TestMode || WindowState != WindowState.Normal || double.IsNaN(Left) || double.IsNaN(Top)) return;
        var bounds = new[] { Left, Top, Width, Height };
        if (_mini) _settings.Update(s => s.MiniBounds = bounds);
        else _settings.Update(s => s.PlayerBounds = bounds);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _tracker?.Flush();
        SaveBounds();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            Web.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogDebug(ex, "Closing WebView2");
        }
        base.OnClosed(e);
    }

    /// <summary>The page for a video: a query the player page reads.</summary>
    internal static string PageFor(string kind, string key, string value, int start) =>
        $"https://{PlayerHost}/player.html?kind={kind}&{key}={Uri.EscapeDataString(value)}&start={start.ToString(CultureInfo.InvariantCulture)}";
}
