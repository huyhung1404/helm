using System.Windows;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Helm.App.Services;

/// <summary>Applies Light/Dark/System theme with the Mica backdrop and follows system changes when asked to.</summary>
internal sealed class ThemeService(ILogger<ThemeService> logger)
{
    private Window? _watched;

    public void Apply(AppTheme theme, Window? window)
    {
        try
        {
            if (_watched is not null)
            {
                SystemThemeWatcher.UnWatch(_watched);
                _watched = null;
            }

            if (theme == AppTheme.System)
            {
                var system = ApplicationThemeManager.GetSystemTheme();
                var appTheme = system is SystemTheme.Light or SystemTheme.Sunrise or SystemTheme.Flow ? ApplicationTheme.Light : ApplicationTheme.Dark;
                ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, updateAccent: true);
                if (window is not null && new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero)
                {
                    SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: true);
                    _watched = window;
                }
            }
            else
            {
                ApplicationThemeManager.Apply(theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
            }

            if (window is not null) ApplicationThemeManager.Apply(window);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply theme {Theme}", theme);
        }
    }
}
