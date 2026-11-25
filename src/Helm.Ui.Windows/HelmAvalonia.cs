using Avalonia;
using Avalonia.Styling;
using Helm.Core.Ui;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Appearance;

namespace Helm.Ui.Windows;

/// <summary>
/// The Avalonia side of the WPF app: one Avalonia Application with Helm's shared theme, started on the WPF UI thread
/// (which pumps Avalonia's messages too) the first time a page is shown, following Helm's light/dark theme.
/// </summary>
public static class HelmAvalonia
{
    private static Exception? s_failed;
    private static bool s_started;

    /// <summary>Where hosted pages report failures; the app sets it at start.</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Starts Avalonia once. Throws (now and on every later call) if it could not start.</summary>
    public static void EnsureStarted()
    {
        if (s_failed is not null) throw new InvalidOperationException("Avalonia could not start.", s_failed);
        if (s_started) return;
        try
        {
            // Avalonia 12 started by hand needs a text shaper (HarfBuzz) as well as the platform and renderer.
            AppBuilder.Configure<HostedApp>().UseWin32().UseSkia().UseHarfBuzz().SetupWithoutStarting();
            s_started = true;
        }
        catch (Exception ex)
        {
            s_failed = ex;
            throw;
        }
        ApplyTheme(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += (theme, _) => ApplyTheme(theme);
    }

    private static void ApplyTheme(ApplicationTheme theme)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = theme == ApplicationTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private sealed class HostedApp : Avalonia.Application
    {
        public override void Initialize() => Styles.Add(new HelmTheme());
    }
}
