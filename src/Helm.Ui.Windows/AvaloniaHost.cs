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
        // While focus is in the page, WPF still matches keys against routed commands up the tree (the tab's Frame binds
        // Backspace to "Back", others Ctrl+C, Delete…). A command that cannot run still marks the key handled, and WPF
        // then never hands the message to the page: Backspace did nothing. Say "not here, keep routing" for every
        // routed command aimed at the page, so the key stays unhandled and reaches Avalonia.
        System.Windows.Input.CommandManager.AddPreviewCanExecuteHandler(this, (_, e) =>
        {
            e.CanExecute = false;
            e.ContinueRouting = true;
            e.Handled = true;
        });
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
        set
        {
            _inner.Content = value;
            if (value is null || !InputLog.On) return;
            value.AttachedToVisualTree += (_, _) =>
            {
                if (Avalonia.Controls.TopLevel.GetTopLevel(value) is { } top && _logged.Add(top)) InputLog.Attach(top, "avalonia");
            };
            // What WPF sees while focus is inside the hosted page, and whether something in WPF takes the key.
            AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
                InputLog.Write($"wpf PreviewKeyDown {e.Key} handled={e.Handled}")), handledEventsToo: true);
            AddHandler(System.Windows.Input.Keyboard.KeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
                InputLog.Write($"wpf KeyDown {e.Key} handled={e.Handled}")), handledEventsToo: true);
            // Who up the tree marks Backspace handled (WPF then never gives the message to the page)?
            Loaded += (_, _) =>
            {
                for (System.Windows.DependencyObject? up = System.Windows.Media.VisualTreeHelper.GetParent(this); up is System.Windows.UIElement element;
                     up = System.Windows.Media.VisualTreeHelper.GetParent(up) ?? (up as System.Windows.FrameworkElement)?.Parent)
                {
                    var name = $"{element.GetType().Name}{((element as System.Windows.FrameworkElement)?.Name is { Length: > 0 } n ? "#" + n : "")}";
                    element.AddHandler(System.Windows.Input.Keyboard.KeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
                    {
                        if (e.Key == System.Windows.Input.Key.Back) InputLog.Write($"wpf   up {name} handled={e.Handled}");
                    }), handledEventsToo: true);
                }
                System.Windows.Input.InputManager.Current.PostProcessInput += (_, e) =>
                {
                    if (e.StagingItem.Input is System.Windows.Input.KeyEventArgs k && k.Key == System.Windows.Input.Key.Back && k.RoutedEvent == System.Windows.Input.Keyboard.KeyDownEvent)
                        InputLog.Write($"wpf   post-process handled={k.Handled}");
                };
            };
        }
    }

    private readonly HashSet<Avalonia.Controls.TopLevel> _logged = [];
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
