using System.Reflection;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Helm.Modules.WatchLater.Player;

/// <summary>Where the Watch Later page shows the player (the page implements it).</summary>
internal interface IPlayerPanel
{
    PlayerView View { get; }

    void ShowPlayer(string title);

    void HidePlayer();

    void ShowRate(double rate);
}

/// <summary>
/// Windows: Play plays the video on the Watch Later page itself, in Helm's window. The mini player (a small window on
/// top of the others) opens only from the player's Mini player button, and Back to Helm returns the video to the page;
/// either way it carries on where it was. A downloaded file plays from disk, offline; otherwise YouTube's or Facebook's
/// own embedded player. Without the WebView2 runtime (rare on Windows 10/11) Play falls back to the browser. UI thread
/// only.
/// </summary>
public sealed class PlayerService : IVideoPlayer
{
    private readonly WatchLaterStore _store;
    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly IWatchDownloads _downloads;
    private readonly IProcessLauncher _launcher;
    private readonly IServiceProvider _services;
    private readonly ILogger<PlayerService> _logger;
    private readonly string _playerFolder;
    private readonly string _webViewData;
    private Task<CoreWebView2Environment>? _environment;
    private bool? _runtime;
    private IPlayerPanel? _panel;
    private PlayerWindow? _window;

    // The shell navigation is resolved when needed: it depends on the module list, which holds this tool.
    public PlayerService(WatchLaterStore store, ISettingsStoreFactory settings, IWatchDownloads downloads, IProcessLauncher launcher, HelmPaths paths,
        IServiceProvider services, ILogger<PlayerService> logger)
    {
        _store = store;
        _settings = settings.Get<WatchLaterSettings>(WatchLaterIds.ModuleId);
        _downloads = downloads;
        _launcher = launcher;
        _services = services;
        _logger = logger;
        var cache = Path.Combine(paths.Root, "cache", WatchLaterIds.ModuleId);
        _playerFolder = Path.Combine(cache, "player");
        _webViewData = Path.Combine(cache, "webview2");
    }

    /// <summary>The speed videos play at (remembered).</summary>
    public double Rate => PlayerView.Speeds.Contains(_settings.Current.PlaybackRate) ? _settings.Current.PlaybackRate : 1;

    /// <summary>True when the WebView2 runtime is installed (checked once).</summary>
    public bool HasRuntime
    {
        get
        {
            if (_runtime is { } known) return known;
            try
            {
                _runtime = !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogInformation("WebView2 is not available: {Message}", ex.Message);
                _runtime = false;
            }
            return _runtime.Value;
        }
    }

    /// <summary>The Watch Later page hands over where it shows the player.</summary>
    internal void AttachPanel(IPlayerPanel panel)
    {
        _panel = panel;
        panel.View.Attach(_store, _launcher, _logger, Environment, _playerFolder);
        panel.ShowRate(Rate);
    }

    public bool CanPlay(WatchItem item) => HasRuntime && SourceFor(item, _store.Find(item.Key)?.Id) is not null;

    public void Play(string id)
    {
        if (_store.Get(id) is not { } item) return;
        if (SourceFor(item, id) is not { } source || !HasRuntime)
        {
            _launcher.OpenUrl(WatchLaterFormat.ResumeUrl(item));
            return;
        }
        try
        {
            WritePlayerPage();
            if (_window is { } window)
            {
                // The mini player is open: the new video plays there.
                window.ShowTitle(item.DisplayTitle);
                window.Player.Play(id, source, Rate);
                return;
            }
            if (_panel is null) return;
            _services.GetRequiredService<IShellNavigation>().ShowPage(typeof(WatchLaterContentPage));
            _panel.ShowPlayer(item.DisplayTitle);
            _panel.View.Play(id, source, Rate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the player failed");
            _launcher.OpenUrl(WatchLaterFormat.ResumeUrl(item));
        }
    }

    /// <summary>The video on the page moves to the mini player, carrying on where it is.</summary>
    internal void PopOut()
    {
        if (_panel?.View.CurrentId is not { } id) return;
        _panel.View.Stop();
        _panel.HidePlayer();
        if (_store.Get(id) is not { } item || SourceFor(item, id) is not { } source) return;
        try
        {
            var window = new PlayerWindow(_settings, Dock);
            window.Player.Attach(_store, _launcher, _logger, Environment, _playerFolder);
            window.ShowTitle(item.DisplayTitle);
            window.SetRate(Rate);
            window.SpeedPicked += (_, rate) => SetRate(rate);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window)) _window = null;
            };
            _window = window;
            window.Show();
            window.Player.Play(id, source, Rate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the mini player failed");
            _window = null;
            Play(id);
        }
    }

    /// <summary>Back to Helm from the mini player: the video carries on on the Watch Later page.</summary>
    private void Dock(string? id)
    {
        var window = _window;
        _window = null;
        window?.Close();
        if (id is not null) Play(id);
    }

    /// <summary>The page's close button: the video stops (where it was is saved).</summary>
    internal void StopPanel()
    {
        _panel?.View.Stop();
        _panel?.HidePlayer();
    }

    /// <summary>Leaving the Watch Later page pauses the video on it.</summary>
    internal void PausePanel() => _panel?.View.Pause();

    /// <summary>The speed for every video from now on, remembered.</summary>
    internal void SetRate(double rate)
    {
        _settings.Update(s => s.PlaybackRate = rate);
        _panel?.View.SetRate(rate);
        _panel?.ShowRate(rate);
        _window?.Player.SetRate(rate);
        _window?.SetRate(rate);
    }

    /// <summary>The tool is off or Helm exits: playback stops everywhere (where it was is saved).</summary>
    public void Close()
    {
        StopPanel();
        try
        {
            _window?.Close();
        }
        catch (InvalidOperationException) { }
        _window = null;
    }

    /// <summary>What to play: the downloaded file, else the site's embed; null when neither can play here.</summary>
    private PlayerSource? SourceFor(WatchItem item, string? id)
    {
        var start = item.ResumeSeconds ?? 0;
        if (id is not null && _downloads.FileFor(id) is { } file && Path.GetDirectoryName(file) is { } folder)
        {
            var src = $"https://{PlayerView.FileHost}/" + Uri.EscapeDataString(Path.GetFileName(file));
            return new PlayerSource(PlayerView.PageFor("file", "src", src, start), folder);
        }
        return item switch
        {
            { Source: WatchSource.YouTube, ExternalId: { Length: > 0 } yt } => new PlayerSource(PlayerView.PageFor("youtube", "id", yt, start), null),
            { Source: WatchSource.Facebook } => new PlayerSource(PlayerView.PageFor("facebook", "href", item.Url, start), null),
            _ => null,
        };
    }

    /// <summary>
    /// One WebView2 environment for the players, with its data in Helm's cache (the install folder may not be
    /// writable) and videos allowed to start with sound (they start from a click on Play).
    /// </summary>
    private Task<CoreWebView2Environment> Environment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, _webViewData,
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));

    private void WritePlayerPage()
    {
        Directory.CreateDirectory(_playerFolder);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Helm.WatchLater.player.html")
                             ?? throw new InvalidOperationException("The player page is missing from the build.");
        using var reader = new StreamReader(resource);
        var page = reader.ReadToEnd();
        var path = Path.Combine(_playerFolder, "player.html");
        if (!File.Exists(path) || File.ReadAllText(path) != page) File.WriteAllText(path, page);
    }
}
