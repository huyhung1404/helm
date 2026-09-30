using System.Reflection;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Helm.Modules.WatchLater.Player;

/// <summary>
/// Windows: Play opens Helm's player window (one at a time; playing another video replaces it). A downloaded file
/// plays from disk, offline; otherwise YouTube's or Facebook's own embedded player. Without the WebView2 runtime
/// (rare on Windows 10/11) Play falls back to the browser. UI thread only.
/// </summary>
public sealed class PlayerService : IVideoPlayer
{
    private readonly WatchLaterStore _store;
    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly IWatchDownloads _downloads;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<PlayerService> _logger;
    private readonly string _playerFolder;
    private readonly string _webViewData;
    private Task<CoreWebView2Environment>? _environment;
    private bool? _runtime;
    private PlayerWindow? _window;

    public PlayerService(WatchLaterStore store, ISettingsStoreFactory settings, IWatchDownloads downloads, IProcessLauncher launcher, HelmPaths paths,
        ILogger<PlayerService> logger)
    {
        _store = store;
        _settings = settings.Get<WatchLaterSettings>(WatchLaterIds.ModuleId);
        _downloads = downloads;
        _launcher = launcher;
        _logger = logger;
        var cache = Path.Combine(paths.Root, "cache", WatchLaterIds.ModuleId);
        _playerFolder = Path.Combine(cache, "player");
        _webViewData = Path.Combine(cache, "webview2");
    }

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
            var window = _window ??= NewWindow();
            window.Play(id, source);
            if (!window.IsVisible) window.Show();
            if (window.WindowState == System.Windows.WindowState.Minimized) window.WindowState = System.Windows.WindowState.Normal;
            if (!PlayerWindow.TestMode) window.Activate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the player failed");
            _launcher.OpenUrl(WatchLaterFormat.ResumeUrl(item));
        }
    }

    /// <summary>The tool is off or Helm exits: the player closes (where it was is saved).</summary>
    public void Close()
    {
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
            var src = $"https://{PlayerWindow.FileHost}/" + Uri.EscapeDataString(Path.GetFileName(file));
            return new PlayerSource(PlayerWindow.PageFor("file", "src", src, start), folder);
        }
        return item switch
        {
            { Source: WatchSource.YouTube, ExternalId: { Length: > 0 } yt } => new PlayerSource(PlayerWindow.PageFor("youtube", "id", yt, start), null),
            { Source: WatchSource.Facebook } => new PlayerSource(PlayerWindow.PageFor("facebook", "href", item.Url, start), null),
            _ => null,
        };
    }

    private PlayerWindow NewWindow()
    {
        var window = new PlayerWindow(_store, _settings, _launcher, _logger, Environment, _playerFolder);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window)) _window = null;
        };
        return window;
    }

    /// <summary>
    /// One WebView2 environment for the player, with its data in Helm's cache (the install folder may not be
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
