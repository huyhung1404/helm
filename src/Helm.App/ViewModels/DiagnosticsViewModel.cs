using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Services;

namespace Helm.App.ViewModels;

/// <summary>Debug page: lists monitors and windows as Helm sees them and lets you watch the input hooks.</summary>
internal sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly LowLevelKeyboardHook _keyboard;
    private readonly LowLevelMouseHook _mouse;
    private readonly IHotkeyManager _hotkeys;
    private readonly IUiDispatcher _ui;
    private IDisposable? _keyboardLease;
    private IDisposable? _mouseLease;
    private long _keyCount;
    private long _mouseCount;

    [ObservableProperty]
    private bool _isHookTestEnabled;

    [ObservableProperty]
    private string _hookStatus = "Hooks idle.";

    [ObservableProperty]
    private string _lastKey = "–";

    [ObservableProperty]
    private string _lastMouse = "–";

    [ObservableProperty]
    private string _summary = string.Empty;

    public DiagnosticsViewModel(
        IWindowService windows,
        IMonitorService monitors,
        LowLevelKeyboardHook keyboard,
        LowLevelMouseHook mouse,
        IHotkeyManager hotkeys,
        IUiDispatcher ui)
    {
        _windows = windows;
        _monitors = monitors;
        _keyboard = keyboard;
        _mouse = mouse;
        _hotkeys = hotkeys;
        _ui = ui;
        Refresh();
    }

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    public ObservableCollection<WindowInfo> Windows { get; } = [];

    public ObservableCollection<string> Conflicts { get; } = [];

    [RelayCommand]
    private void Refresh()
    {
        Monitors.Clear();
        foreach (var m in _monitors.GetMonitors()) Monitors.Add(m);

        Windows.Clear();
        foreach (var w in _windows.GetAppWindows()) Windows.Add(w);

        Conflicts.Clear();
        foreach (var c in _hotkeys.Conflicts) Conflicts.Add(c.Describe());

        var fg = _windows.GetForegroundWindow();
        Summary = $"{Monitors.Count} monitor(s), {Windows.Count} app window(s). Foreground: 0x{fg:X} {_windows.GetTitle(fg)}";
    }

    partial void OnIsHookTestEnabledChanged(bool value)
    {
        if (value)
        {
            try
            {
                _keyboard.Observed += OnKey;
                _mouse.Observed += OnMouse;
                _keyboardLease = _keyboard.Acquire();
                _mouseLease = _mouse.Acquire();
                HookStatus = "Keyboard and mouse hooks installed — type or move the mouse anywhere.";
            }
            catch (HookInstallException ex)
            {
                HookStatus = ex.Message;
                StopHooks();
            }
        }
        else
        {
            StopHooks();
            HookStatus = "Hooks idle.";
        }
    }

    private void StopHooks()
    {
        _keyboard.Observed -= OnKey;
        _mouse.Observed -= OnMouse;
        _keyboardLease?.Dispose();
        _mouseLease?.Dispose();
        _keyboardLease = _mouseLease = null;
    }

    private void OnKey(object? sender, KeyboardHookEventArgs e)
    {
        var n = Interlocked.Increment(ref _keyCount);
        var text = $"#{n} {VirtualKeyNames.GetName(e.VirtualKey)} {(e.IsKeyDown ? "down" : "up")}{(e.IsInjected ? " (injected)" : string.Empty)}";
        _ui.Post(() => LastKey = text);
    }

    private void OnMouse(object? sender, MouseHookEventArgs e)
    {
        var n = Interlocked.Increment(ref _mouseCount);
        if (e.Message == MouseMessage.Move && n % 10 != 0) return; // throttle UI updates
        var text = $"#{n} {e.Message} at {e.Position}";
        _ui.Post(() => LastMouse = text);
    }
}
