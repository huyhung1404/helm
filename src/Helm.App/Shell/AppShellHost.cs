using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.General;
using Helm.Shell.Search;
using Helm.Shell.Tabs;
using Helm.Shell.Views;
using Helm.Shell.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.App.Shell;

/// <summary>
/// The shell's side of the app: the one <see cref="ShellViewModel"/> (menu, tabs of every window, search, sync button),
/// the pages it asks for (the shell's own from <see cref="WindowsShellPages"/>, each tool's from
/// <see cref="IHelmModule.CreatePage"/>), a window per torn-off tab, and navigation for the tools
/// (<see cref="IShellNavigation.ShowTool"/>) and Home and General (<see cref="IShellWindowsHost"/>).
/// </summary>
internal sealed class AppShellHost(IServiceProvider services, ILogger<AppShellHost> logger) : IShellHost, IShellNavigation, IShellWindowsHost
{
    private readonly Dictionary<TabStrip, HelmWindow> _windows = [];
    private ShellViewModel? _shell;

    /// <summary>The shell, made on first use (after the tools are registered).</summary>
    public ShellViewModel Shell => _shell ??= Build();

    /// <summary>The main window, set by the shell controller once it made it.</summary>
    public MainWindow? MainWindow { get; set; }

    private ShellViewModel Build()
    {
        var modules = services.GetRequiredService<IModuleHost>().Modules;
        var tools = modules.Select(ToolFor).ToList();
        var search = new SettingsSearch([new PagesSearchSource(modules, () => services.GetRequiredService<GeneralComposer>().Model)]);
        var sync = services.GetRequiredService<SyncIndicatorViewModel>();
        sync.OpenSettings = () => services.GetRequiredService<Helm.Shell.Home.IHomeLinks>().OpenGeneral("sync");
        return new ShellViewModel(this, tools, search, sync);
    }

    /// <summary>A tool in the menu: its picture icon (else its glyph), kept in step with its on/off switch.</summary>
    private static ShellToolItem ToolFor(IHelmModule module)
    {
        var item = new ShellToolItem(module.Id, module.DisplayName, module.Group, module.IconImage, module.IsEnabled) { Glyph = module.Icon };
        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) item.IsEnabled = module.IsEnabled;
        };
        return item;
    }

    // ---- IShellHost ----------------------------------------------------------------------------------------------

    public Control CreatePage(string key)
    {
        try
        {
            if (services.GetRequiredService<WindowsShellPages>().Create(key) is { } page) return page;
            if (services.GetRequiredService<IModuleHost>().Find(key) is { } module) return module.CreatePage();
            return Problem($"No page called “{key}”.");
        }
        catch (Exception ex)
        {
            // One page that cannot open never takes the shell down: it says why in its place.
            logger.LogError(ex, "The page {Page} could not open", key);
            return Problem($"This page could not open: {ex.GetBaseException().Message}");
        }
    }

    public void Reveal(string key, Control page, string anchor)
    {
        if (key == ShellPages.General && page.DataContext is GeneralViewModel general
            && general.Sections.FirstOrDefault(s => s.Id == anchor) is { } section)
            general.Selected = section;
    }

    public void ReportBug() => services.GetRequiredService<BugReports.PcBugReporter>().Report();

    public void OpenWindow(TabStrip strip)
    {
        var view = new ShellView(Shell, Shell.StripFor(strip));
        var window = new HelmWindow(view) { Width = 900, Height = 700 };
        _windows[strip] = window;
        window.Closed += (_, _) =>
        {
            if (_windows.Remove(strip)) Shell.Tabs.WindowClosed(strip);
        };
        window.Activated += (_, _) => Shell.Tabs.Touch(strip);
        // A torn-off tab opens under the mouse, as a browser's does.
        if (MainWindow is { } main)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = main.Position + new PixelPoint(60, 60);
        }
        window.Show();
    }

    public void CloseWindow(TabStrip strip)
    {
        if (_windows.Remove(strip, out var window)) window.Close();
    }

    public void ShowWindow(TabStrip strip)
    {
        if (ReferenceEquals(strip, Shell.Tabs.Main))
        {
            services.GetRequiredService<ShellController>().ShowMainWindow();
            return;
        }
        if (_windows.TryGetValue(strip, out var window)) Bring(window);
    }

    // ---- IShellNavigation, IShellWindowsHost --------------------------------------------------------------------

    public void ShowPage(Type pageType) => logger.LogWarning("ShowPage({Page}) has no meaning on Windows; use ShowTool", pageType.Name);

    public void ShowTool(string key) => Dispatcher.UIThread.Post(() => Open(key));

    public void OpenPage(string key) => Open(key);

    public void OpenGeneral() => Open(ShellPages.General);

    public Task RestartAsync() => services.GetRequiredService<ShellController>().RestartAsync();

    /// <summary>Opens a page's tab in front and brings its window up (the main window for a page not open yet).</summary>
    private void Open(string key)
    {
        if (!Shell.Catalog.Knows(key)) return;
        var tab = Shell.Tabs.Find(key);
        Shell.Open(key, tab?.Strip);
        if (tab?.Strip is { } strip && !ReferenceEquals(strip, Shell.Tabs.Main)) ShowWindow(strip);
        else services.GetRequiredService<ShellController>().ShowMainWindow();
    }

    internal static void Bring(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // In front even when another app has the foreground.
        window.Topmost = true;
        window.Topmost = false;
    }

    private static Control Problem(string text) => new Border
    {
        Padding = new Thickness(40),
        Child = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } },
    };
}
