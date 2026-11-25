using System.Windows.Forms.Integration;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Win32.Interoperability;
using Wpf.Ui.Appearance;

namespace Helm.Ui.Windows;

/// <summary>
/// A WPF element that shows one shared Avalonia page. Starts Avalonia the first time one is created (on the WPF UI
/// thread, which pumps Avalonia's messages too) and keeps its light/dark theme in step with Helm's.
/// </summary>
public sealed class AvaloniaHost : WindowsFormsHost
{
    private readonly WinFormsAvaloniaControlHost _inner = new() { Dock = System.Windows.Forms.DockStyle.Fill };

    public AvaloniaHost()
    {
        HelmAvalonia.EnsureStarted();
        Child = _inner;
        // The hosted window is a real HWND: it does not follow WPF render transforms such as the tab's slide-in, and
        // stays where the animation started. Re-place it once the animation is over (a layout pass re-syncs it).
        Loaded += (_, _) =>
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                InvalidateMeasure();
                UpdateLayout();
            };
            timer.Start();
        };
    }

    public Avalonia.Controls.Control? Page
    {
        get => _inner.Content;
        set => _inner.Content = value;
    }
}

/// <summary>The Avalonia side of the WPF app: one Application with Helm's shared theme.</summary>
public static class HelmAvalonia
{
    private static bool _started;

    public static void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        AppBuilder.Configure<HostedApp>().UseWin32().UseSkia().UseHarfBuzz().SetupWithoutStarting();
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
        public override void Initialize() =>
            Styles.Add(new StyleInclude(new Uri("avares://Helm.Ui.Windows/")) { Source = new Uri("avares://Helm.Ui/HelmTheme.axaml") });
    }
}
