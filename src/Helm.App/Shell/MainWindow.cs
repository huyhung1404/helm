using Avalonia;
using Avalonia.Controls;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Shell.Tabs;
using Helm.Shell.Views;

namespace Helm.App.Shell;

/// <summary>
/// Helm's main window: the shell with the menu and the main tabs. It remembers its size, place and tabs (general.json);
/// closing it only hides it (Helm stays in the tray) unless Helm is exiting.
/// </summary>
internal sealed class MainWindow : HelmWindow
{
    private readonly ShellViewModel _shell;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly Func<bool> _exiting;

    public MainWindow(ShellViewModel shell, ISettingsStore<GeneralSettings> general, IModuleHost modules, Func<bool> exiting, string? startPage)
        : base(new ShellView(shell))
    {
        _shell = shell;
        _general = general;
        _exiting = exiting;
        RestorePlacement();
        var window = general.Current.Window;
        shell.Menu.IsPinned = window.NavPinned;
        if (window.NavWidth is >= 220 and <= 420) shell.Menu.OpenWidth = window.NavWidth;
        shell.Restore(SavedTabs(window, modules), startPage);
        Activated += (_, _) => shell.Tabs.Touch(shell.Tabs.Main);
    }

    /// <summary>Remembers size, place, the menu and the tabs.</summary>
    public void SavePlacement()
    {
        var tabs = _shell.Tabs.Save();
        var normal = WindowState == WindowState.Normal;
        var size = normal ? new Size(Width, Height) : (Size?)null;
        _general.Update(s =>
        {
            s.Window.Tabs = [.. tabs.Keys];
            s.Window.ActiveTab = tabs.Active;
            s.Window.NavPinned = _shell.Menu.IsPinned;
            s.Window.NavWidth = _shell.Menu.OpenWidth;
            s.Window.IsMaximized = WindowState == WindowState.Maximized;
            if (normal && size is { } sz && double.IsFinite(sz.Width) && double.IsFinite(sz.Height))
            {
                s.Window.Left = Position.X;
                s.Window.Top = Position.Y;
                s.Window.Width = sz.Width;
                s.Window.Height = sz.Height;
            }
        });
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SavePlacement();
        if (!_exiting())
        {
            // Closing the window only hides it; Helm keeps running in the tray.
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void RestorePlacement()
    {
        var p = _general.Current.Window;
        Width = double.IsFinite(p.Width) && p.Width >= MinWidth ? p.Width : 1100;
        Height = double.IsFinite(p.Height) && p.Height >= MinHeight ? p.Height : 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (p.Left is { } left && p.Top is { } top && double.IsFinite(left) && double.IsFinite(top))
        {
            var at = new PixelPoint((int)left, (int)top);
            // Only where the title bar is still on a screen of today's monitor setup.
            if (Screens.All.Any(s => s.WorkingArea.Contains(at + new PixelPoint(100, 20))))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = at;
            }
        }
        if (p.IsMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// The tabs of the last session. Keys now; the WPF Helm saved page type names (Helm.Modules.Habits.HabitsContentPage,
    /// Helm.Shell.Windows.HomePage), which map to the tool of that namespace or the shell page of that name.
    /// </summary>
    internal static TabsState? SavedTabs(WindowPlacementSettings window, IModuleHost modules)
    {
        if (window.Tabs is not { Count: > 0 } saved) return null;
        string? Key(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!name.Contains('.')) return name;
            if (name.EndsWith(".HomePage", StringComparison.Ordinal)) return ShellPages.Home;
            if (name.EndsWith(".GeneralPage", StringComparison.Ordinal)) return ShellPages.General;
            if (name.EndsWith(".BugReportsPage", StringComparison.Ordinal)) return ShellPages.BugReports;
            var space = name[..name.LastIndexOf('.')];
            return modules.Modules.FirstOrDefault(m => m.GetType().Namespace == space)?.Id;
        }
        var keys = saved.Select(Key).OfType<string>().Distinct().ToList();
        return keys.Count == 0 ? null : new TabsState(keys, Key(window.ActiveTab));
    }
}
