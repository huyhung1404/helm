using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Helm.App.ViewModels;
using Helm.App.Views.Pages;
using Helm.App.Views.Tabs;
using Helm.Core.Desktop;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Helm.App.Services;

/// <summary>What a page looks like on its tab: its title and a fresh icon (an icon element can sit in one place only).</summary>
internal sealed record TabLook(string Title, Func<IconElement> Icon);

/// <summary>
/// The tabs of every Helm window, like a browser's: each page (Home, General, a tool's content or settings page) opens
/// at most once, in one tab of one window; opening it again goes to that tab. A tab can be dragged along its strip,
/// out of it into a new window, and onto another window's strip. The main window keeps its tabs across restarts.
///
/// Pages are DI singletons, and each keeps its own presenter (a WPF-UI frame) for the app's life, so moving or
/// reopening a tab never navigates the page again: the presenter is only taken out of one window and put into another.
/// </summary>
internal sealed class TabManager(IServiceProvider services, MainWindowViewModel catalog, ISettingsStoreFactory settings, IWindowService windows)
{
    private readonly List<TabHost> _hosts = [];
    private readonly Dictionary<Type, ToolTab> _tabs = [];
    private readonly ISettingsStore<GeneralSettings> _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
    private TabHost? _main;
    private TabHost? _lastActive;

    /// <summary>The main window's tab host.</summary>
    public TabHost Main => _main ?? throw new InvalidOperationException("The main window has not attached its tabs yet");

    /// <summary>False until the main window exists (it attaches its tabs when it is created).</summary>
    public bool IsAttached => _main is not null;

    /// <summary>A tab of the main window became the shown one (the menu highlights its page).</summary>
    public event EventHandler<Type?>? MainActivePageChanged;

    public void AttachMain(TabHost host)
    {
        _main = host;
        Register(host);
    }

    /// <summary>Restores the main window's tabs from the last session, or opens <paramref name="startPage"/> (or Home).</summary>
    public void RestoreMain(Type? startPage)
    {
        foreach (var name in _general.Current.Window.Tabs ?? [])
            if (Resolve(name) is { } type) Open(type, Main, show: false);
        var shown = startPage ?? Resolve(_general.Current.Window.ActiveTab);
        if (shown is not null) Main.Activate(Open(shown, Main, show: false));
        else if (Main.Tabs.Count > 0) Main.Activate(Main.Tabs[0]);
        else Main.Activate(Open(typeof(HomePage), Main, show: false));
    }

    /// <summary>Remembers the main window's tabs (general.json); other windows are not restored.</summary>
    public void SaveMain()
    {
        if (_main is null) return;
        var tabs = _main.Tabs.Select(t => t.PageType.FullName!).ToList();
        var active = _main.ActiveTab?.PageType.FullName;
        _general.Update(s =>
        {
            s.Window.Tabs = tabs;
            s.Window.ActiveTab = active;
        });
    }

    /// <summary>
    /// Shows <paramref name="pageType"/>: its tab wherever it is, else a new tab in <paramref name="target"/> (by default
    /// the window used last, or the main one), and brings that window to the front unless <paramref name="show"/> is false.
    /// </summary>
    public ToolTab Open(Type pageType, TabHost? target = null, bool show = true)
    {
        if (!_tabs.TryGetValue(pageType, out var tab))
            tab = _tabs[pageType] = new ToolTab(pageType, Describe(pageType), () => services.GetRequiredService(pageType));
        if (tab.Host is null)
        {
            target ??= _lastActive is { } last && Window.GetWindow(last) is { IsVisible: true } ? last : Main;
            target.Insert(tab, target.Tabs.Count);
        }
        if (show) Show(tab);
        return tab;
    }

    /// <summary>Selects the tab and brings its window to the front.</summary>
    public void Show(ToolTab tab)
    {
        if (tab.Host is not { } host) return;
        host.Activate(tab);
        if (ReferenceEquals(host, _main)) services.GetRequiredService<ShellController>().ShowMainWindow();
        else if (Window.GetWindow(host) is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    public void Close(ToolTab tab)
    {
        if (tab.Host is not { } host) return;
        host.Remove(tab);
        AfterRemoved(host);
    }

    public void CloseOthers(ToolTab keep)
    {
        if (keep.Host is not { } host) return;
        foreach (var tab in host.Tabs.Where(t => t != keep).ToList()) host.Remove(tab);
        host.Activate(keep);
    }

    /// <summary>Moves a tab to another window's strip (dropped there, or "Move to main window"), at <paramref name="index"/>.</summary>
    public void Move(ToolTab tab, TabHost target, int index)
    {
        if (tab.Host is not { } source) return;
        if (ReferenceEquals(source, target))
        {
            source.MoveTo(tab, index);
            return;
        }
        source.Remove(tab);
        target.Insert(tab, index);
        Show(tab);
        AfterRemoved(source);
    }

    /// <summary>
    /// Takes the tab out of its window into a new one under the cursor, then lets the window follow the mouse until
    /// the button is released; dropped on another window's tab strip, the tab joins that window instead.
    /// </summary>
    /// <param name="grab">Where the cursor holds the tab, relative to the tab's top-left corner (DIPs).</param>
    public void TearOff(ToolTab tab, Point grab)
    {
        if (tab.Host is not { } source || Window.GetWindow(source) is not { } from) return;
        var cursor = Cursor();
        var dpi = VisualTreeHelper.GetDpi(from);
        // The tab ends up under the cursor, where it was held.
        var at = new Point(cursor.X / dpi.DpiScaleX - TabHost.StripLeft - grab.X,
            cursor.Y / dpi.DpiScaleY - TabWindow.TitleBarHeight - TabHost.StripTop - grab.Y);
        FollowMouse(NewWindow(tab, at));
    }

    /// <summary>The tab's context menu: a new window a little below and right of the current one.</summary>
    public void MoveToNewWindow(ToolTab tab)
    {
        if (tab.Host is not { } source || Window.GetWindow(source) is not { } from) return;
        var origin = from.WindowState == WindowState.Normal ? new Point(from.Left, from.Top) : from.RestoreBounds.TopLeft;
        NewWindow(tab, new Point(origin.X + 40, origin.Y + 40));
    }

    private TabWindow NewWindow(ToolTab tab, Point topLeft)
    {
        var source = tab.Host!;
        var from = Window.GetWindow(source)!;
        var window = new TabWindow(this);
        var size = from.WindowState == WindowState.Normal ? new Size(from.ActualWidth, from.ActualHeight) : from.RestoreBounds.Size;
        // Without the main window's menu the tool needs less room.
        window.Width = Math.Max(window.MinWidth, ReferenceEquals(source, _main) ? source.ActualWidth : size.Width);
        window.Height = Math.Max(window.MinHeight, size.Height);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = topLeft.X;
        window.Top = topLeft.Y;

        source.Remove(tab);
        window.Host.Insert(tab, 0);
        window.Host.Activate(tab);
        window.Show();
        window.Activate();
        AfterRemoved(source);
        return window;
    }

    /// <summary>A window holding its only tab: dragging that tab drags the window, and it can be dropped onto a strip.</summary>
    public void DragWindow(TabWindow window) => FollowMouse(window);

    private void FollowMouse(TabWindow window)
    {
        if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            try
            {
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button was released before the move loop started.
            }
        }

        // Dropped on another window's strip: the tabs join it, and this window goes.
        var cursor = Cursor();
        var target = _hosts.FirstOrDefault(h => !ReferenceEquals(h, window.Host) && h.StripContains(cursor));
        if (target is null) return;
        var index = target.IndexAt(cursor);
        foreach (var tab in window.Host.Tabs.ToList())
        {
            window.Host.Remove(tab);
            target.Insert(tab, index++);
            Show(tab);
        }
        window.Close();
    }

    /// <summary>The tab strip's "+" menu: every page a tab can open, Home first.</summary>
    public IEnumerable<(TabLook Look, Type PageType)> Launchable()
    {
        yield return (Describe(typeof(HomePage)), typeof(HomePage));
        foreach (var module in catalog.Groups.SelectMany(g => g.Modules).OrderBy(m => m.DisplayName))
        {
            var page = (module as IModuleContent)?.ContentPageType ?? module.SettingsPageType;
            yield return (Describe(page), page);
        }
        yield return (Describe(typeof(GeneralPage)), typeof(GeneralPage));
        yield return (Describe(typeof(McpPage)), typeof(McpPage));
    }

    internal void Register(TabHost host)
    {
        _hosts.Add(host);
        host.Changed += (_, _) =>
        {
            if (ReferenceEquals(host, _main)) MainActivePageChanged?.Invoke(this, host.ActiveTab?.PageType);
        };
        host.Activated += (_, _) => _lastActive = host;
    }

    /// <summary>A window closed: its tabs close with it (each page waits, as it was, for the next time it opens).</summary>
    internal void Unregister(TabHost host)
    {
        foreach (var tab in host.Tabs.ToList()) host.Remove(tab);
        _hosts.Remove(host);
        if (ReferenceEquals(_lastActive, host)) _lastActive = null;
    }

    /// <summary>The main window never stays empty (Home comes back); another window closes with its last tab.</summary>
    private void AfterRemoved(TabHost host)
    {
        if (host.Tabs.Count > 0) return;
        if (ReferenceEquals(host, _main)) host.Activate(Open(typeof(HomePage), host, show: false));
        else if (Window.GetWindow(host) is { } window) window.Close();
    }

    public TabLook Describe(Type pageType)
    {
        if (pageType == typeof(HomePage)) return new("Home", () => new SymbolIcon(SymbolRegular.Home24));
        if (pageType == typeof(GeneralPage)) return new("General", () => new SymbolIcon(SymbolRegular.Settings24));
        if (pageType == typeof(McpPage)) return new("AI & MCP", () => new SymbolIcon(SymbolRegular.PlugConnected24));
        if (pageType == typeof(WhatsNewPage)) return new("What's new", () => new SymbolIcon(SymbolRegular.Megaphone24));
        if (pageType == typeof(WelcomePage)) return new("Welcome", () => new SymbolIcon(SymbolRegular.HandWave24));
        if (pageType == typeof(DiagnosticsPage)) return new("Diagnostics", () => new SymbolIcon(SymbolRegular.Bug24));
        foreach (var module in catalog.Groups.SelectMany(g => g.Modules))
        {
            var content = (module as IModuleContent)?.ContentPageType;
            if (pageType != content && pageType != module.SettingsPageType) continue;
            // A tool with content has its options on a second page: its tab says so.
            var title = content is not null && pageType == module.SettingsPageType ? $"{module.DisplayName} settings" : module.DisplayName;
            return new(title, () => ModuleIcon.Create(module, imageSize: 16));
        }
        var page = services.GetService(pageType) as Page;
        return new(page?.Title ?? pageType.Name, () => new SymbolIcon(SymbolRegular.Document24));
    }

    /// <summary>The cursor in screen pixels (WPF knows no position outside its windows while nothing is captured).</summary>
    private Point Cursor()
    {
        var p = windows.GetCursorPosition();
        return new Point(p.X, p.Y);
    }

    private Type? Resolve(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return Launchable().Select(l => l.PageType)
            .Concat(catalog.Groups.SelectMany(g => g.Modules).Select(m => m.SettingsPageType))
            .Concat([typeof(WhatsNewPage)])
            .FirstOrDefault(t => t.FullName == name);
    }
}
