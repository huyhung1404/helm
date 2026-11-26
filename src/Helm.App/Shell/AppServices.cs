using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Helm.Ui.Windows;

namespace Helm.App.Shell;

/// <summary>Helm's UI thread is Avalonia's.</summary>
internal sealed class UiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action)
    {
        if (!CheckAccess()) return Dispatcher.UIThread.InvokeAsync(action).GetTask();
        action();
        return Task.CompletedTask;
    }
}

/// <summary>Confirmations as Helm's glass sheet over the window in front (or the main window).</summary>
internal sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            DesktopWindows.Active?.Content is Control anchor
                ? new GlassDialogs(() => anchor).ConfirmAsync(title, message, confirmText)
                : Task.FromResult(false)).GetTask().Unwrap();
}

internal sealed class ClipboardService : IClipboardService
{
    // The clipboard can be busy; the text then stays selectable where it was.
    public void SetText(string text) => WindowsClipboard.SetText(text);
}

internal sealed class DeviceInfo : IDeviceInfo
{
    public string DeviceName => Environment.MachineName;
}

/// <summary>Light, Dark, or the system's (Avalonia follows the system's switch by itself).</summary>
internal sealed class ThemeService
{
    public void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
