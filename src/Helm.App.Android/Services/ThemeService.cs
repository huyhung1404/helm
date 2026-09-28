using Avalonia;
using Avalonia.Styling;
using Helm.Core.Settings;

namespace Helm.App.Android.Services;

/// <summary>Light, Dark, or System (follows the Android dark theme setting).</summary>
public sealed class ThemeService
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
