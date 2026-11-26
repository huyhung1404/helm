using System.Runtime.InteropServices;
using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Diagnostics;
using Helm.Shell.Services;

namespace Helm.Shell.Windows;

/// <summary>The Diagnostics page's facts on Windows: Win32's monitors and windows, the low-level hooks, the shortcuts taken.</summary>
public sealed class WindowsDiagnosticsSource(
    IWindowService windows,
    IMonitorService monitors,
    LowLevelKeyboardHook keyboard,
    LowLevelMouseHook mouse,
    IHotkeyManager hotkeys,
    ISettingsStoreFactory settings,
    IProcessLauncher launcher) : IDiagnosticsSource
{
    public IReadOnlyList<DiagnosticsFact> Facts() =>
    [
        new("Version", $"Helm {AppInfo.Version}"),
        new("Program", launcher.ExecutablePath),
        new("Data folder", settings.Paths.Root, IsFolder: true),
        new("Settings", settings.Paths.SettingsDirectory, IsFolder: true),
        new("Logs", settings.Paths.LogsDirectory, IsFolder: true),
        new("Runs as administrator", launcher.IsElevated ? "Yes" : "No"),
        new("Windows", RuntimeInformation.OSDescription),
        new(".NET", RuntimeInformation.FrameworkDescription),
    ];

    public IReadOnlyList<MonitorInfo> Monitors() => monitors.GetMonitors();

    public IReadOnlyList<WindowInfo> Windows() => windows.GetAppWindows();

    public IReadOnlyList<string> Conflicts() => hotkeys.Conflicts.Select(c => c.Describe()).ToList();

    public (nint Handle, string Title) Foreground()
    {
        var front = windows.GetForegroundWindow();
        return (front, windows.GetTitle(front));
    }

    public IDisposable WatchInput(Action<string> key, Action<string> move)
    {
        void OnKey(object? sender, KeyboardHookEventArgs e) =>
            key($"{VirtualKeyNames.GetName(e.VirtualKey)} {(e.IsKeyDown ? "down" : "up")}{(e.IsInjected ? " (injected)" : string.Empty)}");
        void OnMouse(object? sender, MouseHookEventArgs e) => move($"{e.Message} at {e.Position}");

        IDisposable? keyLease = null, mouseLease = null;
        keyboard.Observed += OnKey;
        mouse.Observed += OnMouse;
        try
        {
            keyLease = keyboard.Acquire();
            mouseLease = mouse.Acquire();
        }
        catch (HookInstallException ex)
        {
            keyboard.Observed -= OnKey;
            mouse.Observed -= OnMouse;
            keyLease?.Dispose();
            throw new InvalidOperationException(ex.Message, ex);
        }
        return new Lease(() =>
        {
            keyboard.Observed -= OnKey;
            mouse.Observed -= OnMouse;
            keyLease.Dispose();
            mouseLease.Dispose();
        });
    }

    public void OpenFolder(string path) => launcher.OpenFolder(path);

    private sealed class Lease(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}
