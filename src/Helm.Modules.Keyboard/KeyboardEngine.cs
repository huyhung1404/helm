using System.Media;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Hooks;
using Helm.Core.Interop;
using Helm.Typing;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Keyboard;

/// <summary>
/// Switches the keyboard: the shortcut goes round English → Vietnamese → Chinese, and every app gets back the keyboard
/// it was left with (English the first time). English and Vietnamese are Helm Keyboard and its V/E switch, a TSF global
/// compartment; Chinese is the Chinese keyboard. Windows has one keyboard for all windows, so the keyboard showing when
/// an app is left is the one it was left with. Everything runs on one <see cref="MessageLoopThread"/>; the keyboard hook
/// only watches the shortcut and posts here.
/// </summary>
internal sealed class KeyboardEngine : IAsyncDisposable
{
    private const int VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;

    /// <summary>A switch Helm asks for shows in the window within this time, or is asked for once more.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(0.6);

    private static readonly TimeSpan IndicatorTime = TimeSpan.FromSeconds(0.9);

    /// <summary>While a browser is in front, how often its address is read again to notice a tab or site change.</summary>
    private static readonly TimeSpan SitePoll = TimeSpan.FromMilliseconds(500);

    private readonly IWindowService _windows;
    private readonly IInputMethods _inputMethods;
    private readonly IMonitorService _monitors;
    private readonly IBrowserSites _sites;
    private readonly KeyboardApps _apps;
    private readonly LowLevelKeyboardHook _keyboard;
    private readonly ILogger _logger;
    private readonly MessageLoopThread _thread;
    private WinEventHook? _foregroundHook;
    private IDisposable? _keyboardLease;
    private IGlobalCompartment? _vietnamese;
    private IGlobalCompartment? _helmSwitching;
    private volatile ModifierTap? _tap; // used on the hook thread only
    private Options _options;
    private string? _app;
    /// <summary>The site the front browser shows ("facebook.com"), or null when the front app is not a browser on a page.</summary>
    private string? _site;
    /// <summary>What the keyboard is remembered against right now: a site key for a browser page, else the app name.</summary>
    private string? _identity;
    private IDisposable? _sitePollTimer;
    private nint _window;
    /// <summary>The keyboard the app in front should type with.</summary>
    private KeyboardMode _mode;
    /// <summary>It does: Helm's last switch showed in the window (or none was needed).</summary>
    private bool _shows;
    private OverlayWindow? _indicator;
    private KeyboardMode _indicated;
    private IDisposable? _indicatorTimer;

    private KeyboardEngine(IWindowService windows, IInputMethods inputMethods, IMonitorService monitors, IBrowserSites sites,
        KeyboardApps apps, LowLevelKeyboardHook keyboard, ILogger logger, Options options)
    {
        _windows = windows;
        _inputMethods = inputMethods;
        _monitors = monitors;
        _sites = sites;
        _apps = apps;
        _keyboard = keyboard;
        _logger = logger;
        _options = options;
        _thread = new MessageLoopThread("Helm.Keyboard", logger);
    }

    public static async Task<KeyboardEngine> StartAsync(IWindowService windows, IInputMethods inputMethods, IMonitorService monitors,
        IBrowserSites sites, KeyboardApps apps, LowLevelKeyboardHook keyboard, ILogger logger, Options options)
    {
        var engine = new KeyboardEngine(windows, inputMethods, monitors, sites, apps, keyboard, logger, options);
        try
        {
            await engine._thread.InvokeAsync(() =>
            {
                engine._vietnamese = inputMethods.OpenGlobalCompartment(KeyboardIds.VietnameseSwitch);
                engine._helmSwitching = inputMethods.OpenGlobalCompartment(KeyboardIds.HelmSwitching);
                // The input method leaves the shortcut to Helm from now on, while this process lives.
                engine._helmSwitching.Write(Environment.ProcessId);
            }).ConfigureAwait(false);
            engine._foregroundHook = await WinEventHook.InstallAsync(engine._thread,
                [(WinEvents.SystemForeground, WinEvents.SystemForeground)], engine.OnWinEvent, logger).ConfigureAwait(false);
            keyboard.Intercept += engine.OnKey;
            engine.WatchShortcut(options.Shortcut);
            engine._thread.Post(() => engine.OnForeground(windows.GetForegroundWindow()));
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return engine;
    }

    public void UpdateOptions(Options options)
    {
        WatchShortcut(options.Shortcut);
        _thread.Post(() => _options = options);
    }

    /// <summary>Goes to the next keyboard in the round for the app in front (the shortcut; callable from any thread).</summary>
    public void SwitchNext() => _thread.Post(() =>
    {
        var hwnd = _windows.GetForegroundWindow();
        var app = _windows.GetProcessName(hwnd);
        var site = _sites.SiteKey(hwnd, app);
        var identity = IdentityOf(app, site);
        // Pressed again before the last switch shows in the window: go on from the last one.
        var current = _shows || identity != _identity ? CurrentMode(hwnd) : _mode;
        var withChinese = _options.IncludeChinese && _inputMethods.HasLanguage(KeyboardModes.ChineseSimplified);
        var mode = KeyboardModes.Next(current, withChinese);
        Apply(hwnd, mode);
        if (!string.IsNullOrEmpty(app))
        {
            _app = app;
            _site = site;
            _identity = identity;
            _window = hwnd;
            _mode = mode;
            // A browser page is remembered by its site, so Facebook and Bilibili keep their own keyboard; the browser
            // itself is never remembered as an app (that would pull every site back to one keyboard). Other apps are
            // remembered by name. A browser window with no readable site keeps the keyboard for this moment only.
            if (site is not null) _apps.Remember(KeyboardApps.SiteKeyOf(site), mode);
            else if (!_sites.IsBrowser(app) && !_options.Exclusions.IsExcluded(app)) _apps.Remember(app, mode);
        }
        _logger.LogDebug("Switched {Identity} to {Mode}", identity ?? app, mode);
        Indicate(hwnd, mode);
        PlaySound(mode);
    });

    public async ValueTask DisposeAsync()
    {
        _keyboard.Intercept -= OnKey;
        _keyboardLease?.Dispose();
        _keyboardLease = null;
        try
        {
            await _thread.InvokeAsync(() =>
            {
                _foregroundHook?.Dispose();
                _sitePollTimer?.Dispose();
                _indicatorTimer?.Dispose();
                _indicator?.Dispose();
                // The input method takes its own shortcut back.
                _helmSwitching?.Write(0);
                _helmSwitching?.Dispose();
                _vietnamese?.Dispose();
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Helm Keyboard did not stop cleanly");
        }
        _thread.Dispose();
    }

    /// <summary>A shortcut made of modifier keys only is watched in the keyboard hook; any other is a registered hotkey.</summary>
    private void WatchShortcut(SwitchShortcut shortcut)
    {
        _tap = shortcut switch
        {
            SwitchShortcut.LeftCtrlShift => new ModifierTap(VK_LCONTROL, VK_LSHIFT),
            SwitchShortcut.RightCtrl => new ModifierTap(VK_RCONTROL),
            _ => null,
        };
        if (_tap is null)
        {
            _keyboardLease?.Dispose();
            _keyboardLease = null;
        }
        else
        {
            _keyboardLease ??= _keyboard.Acquire();
        }
    }

    // On the hook thread: must return at once.
    private void OnKey(object? sender, KeyboardHookEventArgs e)
    {
        if (e.ExtraInfo == InputSimulator.HelmSignature) return;
        if (_tap?.OnKey(e.VirtualKey, e.IsKeyDown) == true) SwitchNext();
    }

    private void OnWinEvent(WinEventArgs e)
    {
        if (e.Event == WinEvents.SystemForeground && e.IsWindowEvent) OnForeground(e.Hwnd);
    }

    private void OnForeground(nint hwnd, int retries = 2)
    {
        if (hwnd == 0) return;
        // The taskbar, Alt+Tab and the like are not apps: the app behind them keeps its keyboard. A window just opened
        // comes to the front before it shows or has a title: look again in a moment.
        if (!_windows.IsAppWindow(hwnd))
        {
            if (retries > 0)
                After(TimeSpan.FromMilliseconds(retries == 2 ? 300 : 1000), () =>
                {
                    if (_windows.GetForegroundWindow() == hwnd) OnForeground(hwnd, retries - 1);
                });
            return;
        }
        var app = _windows.GetProcessName(hwnd);
        if (string.IsNullOrEmpty(app)) return;
        var site = _sites.SiteKey(hwnd, app);
        WatchSite(_sites.IsBrowser(app));
        Settle(hwnd, app, site);
    }

    /// <summary>Starts or stops the timer that re-reads a browser's address, so a tab or site change is noticed.</summary>
    private void WatchSite(bool browserInFront)
    {
        if (browserInFront && _sitePollTimer is null)
            _sitePollTimer = _thread.StartTimer(SitePoll, PollSite);
        else if (!browserInFront && _sitePollTimer is not null)
        {
            _sitePollTimer.Dispose();
            _sitePollTimer = null;
        }
    }

    /// <summary>On the engine thread, on a timer: if the front browser now shows a different site, treat it as new.</summary>
    private void PollSite()
    {
        var hwnd = _windows.GetForegroundWindow();
        if (hwnd != _window || !_sites.IsBrowser(_app ?? "")) return;
        var site = _sites.SiteKey(hwnd, _app!);
        if (site != _site) Settle(hwnd, _app!, site);
    }

    /// <summary>
    /// Gives <paramref name="hwnd"/> the keyboard its identity (its site when a browser is on a page, else its app)
    /// was last left with. A browser page never seen before, or a browser window with no readable site, keeps the
    /// keyboard it has now — so typing is never pulled to another keyboard the moment a page opens or a window pops up.
    /// </summary>
    private void Settle(nint hwnd, string app, string? site)
    {
        var identity = IdentityOf(app, site);
        if (identity == _identity && SameApp(app, _app) && site == _site) return;

        // The keyboard the last identity was left with, maybe changed some other way (the V/E button, Win+Space): read
        // from its own window, as the new one may still show its own last keyboard. Only when Helm's own switch had
        // shown, and only for an identity Helm remembers (a site, or a non-browser app): one that did not take is not
        // the user's choice, and a browser window with no site is not something we remember. A site is only learnt back
        // once the user has set it by hand (the shortcut): merely passing through a page on some keyboard never pins it.
        if (_identity is not null && _shows && !_options.Exclusions.IsExcluded(_app ?? "")
            && (_site is null || _apps.Knows(_site))
            && _inputMethods.GetLanguage(_window) is var language and not 0)
        {
            var left = KeyboardModes.From(language, _vietnamese?.Read() == 1);
            if (left != _mode) _apps.Remember(_identity, left);
        }

        _app = app;
        _site = site;
        _identity = identity;
        _window = hwnd;
        var current = CurrentMode(hwnd);

        // No identity to go by (a browser window with no readable page), or a known app that is excluded, or a browser
        // page never seen before: leave the keyboard as it is and remember what it is for next time this identity leaves.
        if (identity is null || _options.Exclusions.IsExcluded(app) || (site is not null && !_apps.Knows(site)))
        {
            _mode = current;
            _shows = true;
            _logger.LogDebug("{Identity} in front: kept {Mode}", identity ?? app, current);
            return;
        }

        _mode = _apps.ModeOf(identity);
        if (_mode == KeyboardMode.Chinese && !_inputMethods.HasLanguage(KeyboardModes.ChineseSimplified)) _mode = KeyboardMode.English;
        _logger.LogDebug("{Identity} in front: {Mode} (was {Current})", identity, _mode, current);
        if (current != _mode) Apply(hwnd, _mode);
        else _shows = true;
    }

    /// <summary>What the keyboard is remembered against: a browser page's site key, else the app name; null when neither.</summary>
    private string? IdentityOf(string app, string? site) =>
        site is not null ? KeyboardApps.SiteKeyOf(site)
        : _sites.IsBrowser(app) ? null  // a browser window with no readable page: nothing to remember it by
        : string.IsNullOrEmpty(app) ? null
        : app;

    /// <summary>Whether the switch to <paramref name="mode"/> showed in the window; if not, asks once more.</summary>
    private void Verify(nint hwnd, KeyboardMode mode, bool again)
    {
        // Another app or another switch since: nothing to check.
        if (_mode != mode || !SameApp(_windows.GetProcessName(hwnd), _app)) return;
        if (CurrentMode(hwnd) == mode)
        {
            _shows = true;
        }
        else if (again)
        {
            _logger.LogDebug("{App} did not switch to {Mode}; asking again", _app, mode);
            Apply(hwnd, mode, again: false);
        }
        else
        {
            _logger.LogWarning("{App} did not switch to {Mode}", _app, mode);
        }
    }

    /// <summary>Runs <paramref name="action"/> once on this thread after <paramref name="delay"/>.</summary>
    private void After(TimeSpan delay, Action action)
    {
        IDisposable? timer = null;
        timer = _thread.StartTimer(delay, () =>
        {
            timer?.Dispose();
            action();
        });
    }

    private KeyboardMode CurrentMode(nint hwnd) =>
        KeyboardModes.From(_inputMethods.GetLanguage(hwnd), _vietnamese?.Read() == 1);

    private void Apply(nint hwnd, KeyboardMode mode, bool again = true)
    {
        var language = _inputMethods.GetLanguage(hwnd);
        if (mode == KeyboardMode.Chinese)
        {
            if (!KeyboardModes.IsChinese(language)) _inputMethods.RequestLanguage(hwnd, KeyboardModes.ChineseSimplified);
        }
        else
        {
            _vietnamese?.Write(mode == KeyboardMode.Vietnamese ? 1 : 0);
            if (language != KeyboardIds.EnglishUs) _inputMethods.RequestLanguage(hwnd, KeyboardIds.EnglishUs);
        }
        // The window switches when it reads the request; until it does, what shows is not this.
        _shows = false;
        After(Settling, () => Verify(hwnd, mode, again));
    }

    private static bool SameApp(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>EN / VI / 中 for a moment at the bottom of the screen the app is on.</summary>
    private void Indicate(nint hwnd, KeyboardMode mode)
    {
        if (!_options.ShowIndicator || _monitors.FromWindow(hwnd) is not { } monitor) return;
        try
        {
            _indicated = mode;
            _indicator ??= OverlayWindow.Create(Paint, alpha: 235, title: "Helm Keyboard", logger: _logger);
            var scale = monitor.Scale;
            int width = (int)(96 * scale), height = (int)(64 * scale);
            var area = monitor.WorkArea;
            var left = area.Left + (area.Width - width) / 2;
            var top = area.Bottom - height - (int)(96 * scale);
            _indicator.SetBounds(PixelRect.FromSize(left, top, width, height));
            _indicator.Invalidate();
            _indicator.Show();
            _indicatorTimer?.Dispose();
            _indicatorTimer = _thread.StartTimer(IndicatorTime, () =>
            {
                _indicator?.Hide();
                _indicatorTimer?.Dispose();
                _indicatorTimer = null;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the keyboard indicator");
        }
    }

    private void Paint(IOverlayCanvas canvas)
    {
        var box = new PixelRect(0, 0, canvas.Width, canvas.Height);
        canvas.Fill(box, new RgbColor(32, 32, 32));
        canvas.Text(box, _indicated.Label(), new RgbColor(255, 255, 255), canvas.Height / 2);
    }

    private void PlaySound(KeyboardMode mode)
    {
        if (!_options.PlaySound) return;
        try
        {
            (mode == KeyboardMode.English ? SystemSounds.Beep : SystemSounds.Asterisk).Play();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not play sound");
        }
    }

    internal sealed record Options(
        SwitchShortcut Shortcut,
        bool IncludeChinese,
        bool ShowIndicator,
        bool PlaySound,
        ProcessExclusions Exclusions)
    {
        public static Options From(KeyboardSettings s) =>
            new(s.Shortcut, s.IncludeChinese, s.ShowIndicator, s.PlaySound, new ProcessExclusions(s.ExcludedApps));
    }
}
