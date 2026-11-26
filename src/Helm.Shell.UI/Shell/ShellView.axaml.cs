using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Helm.Core.Ui;

namespace Helm.Shell.Views;

/// <summary>
/// A Helm window's content, the same view on every platform: the title bar, the menu, a window's tabs and the shown
/// page (any Avalonia control). The main window shows the menu and the search; a torn-off tab's window
/// (<paramref name="strip"/> given) has only its tabs. Below 720 px it narrows for a phone: the menu becomes a drawer
/// and the tab strip a count in the title bar.
/// </summary>
public partial class ShellView : UserControl
{
    public const double Narrow = 720;
    private static readonly TimeSpan DrawerOpen = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DrawerClose = TimeSpan.FromMilliseconds(160);

    private readonly ShellViewModel? _shell;
    private readonly TabStripViewModel? _strip;
    private readonly TranslateTransform _drawerShift = new();
    private Rect _canvas;
    private bool _narrow;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public ShellView() => InitializeComponent();

    public ShellView(ShellViewModel shell, TabStripViewModel? strip = null)
    {
        _shell = shell;
        _strip = strip ?? shell.Main;
        DataContext = shell;
        InitializeComponent();
        IsMainWindow = strip is null || ReferenceEquals(strip, shell.Main);
        Strip.DataContext = _strip;
        Menu.IsVisible = IsMainWindow;
        Menu.RenderTransform = _drawerShift;
        Menu.Peek.Watch(TitleBar.MenuTrigger);
        Scrim.PointerPressed += (_, _) => shell.Menu.IsDrawerOpen = false;
        TitleBar.TabsButton.Click += (_, _) => TabSwitcher.Show(TitleBar.TabsButton, _strip);
        _strip.PropertyChanged += OnStripChanged;
        shell.Menu.PropertyChanged += OnMenuChanged;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        LayoutUpdated += (_, _) => AlignPageBackdrop();
        ShowPage(_strip.ActivePage);
        Layout();
    }

    /// <summary>The main window (menu, search) rather than a torn-off tab's.</summary>
    public bool IsMainWindow { get; } = true;

    /// <summary>
    /// Draws Helm's own minimize, maximize and close buttons (Windows, Linux). Off on macOS (its own buttons stay) and
    /// on a phone.
    /// </summary>
    public bool ShowWindowButtons
    {
        get => _windowButtons;
        set
        {
            _windowButtons = value;
            Layout();
        }
    }

    private bool _windowButtons = true;

    /// <summary>The page shown right now (the window's active tab).</summary>
    public Control? ShownPage => PageHost.Content as Control;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        if (narrow == _narrow && e.PreviousSize != default) return;
        _narrow = narrow;
        if (_shell is not null && IsMainWindow) _shell.Menu.IsDrawer = narrow;
        Layout();
    }

    private void OnStripChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabStripViewModel.ActivePage)) ShowPage(_strip!.ActivePage);
        if (e.PropertyName is nameof(TabStripViewModel.ActiveTitle) or nameof(TabStripViewModel.Count)) Layout();
    }

    private void OnMenuChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NavMenuViewModel.IsDrawerOpen) or nameof(NavMenuViewModel.ReservedWidth) or nameof(NavMenuViewModel.OpenWidth)) Layout();
    }

    /// <summary>The window's page: taken from wherever it was shown before (another window), then brought into focus.</summary>
    private void ShowPage(Control? page)
    {
        if (ReferenceEquals(PageHost.Content, page)) return;
        if (page?.Parent is ContentControl other && !ReferenceEquals(other, PageHost)) other.Content = null;
        PageHost.Content = page;
        if (page is not null) Motion.EnterPage(page);
    }

    /// <summary>Puts the parts where this window's width and kind want them.</summary>
    private void Layout()
    {
        if (_shell is null || _strip is null) return;
        var menu = _shell.Menu;
        Classes.Set("narrow", _narrow);
        var withMenu = IsMainWindow && !_narrow;
        MenuSpace.IsVisible = withMenu;
        Strip.IsVisible = !_narrow;
        Strip.Margin = withMenu ? new Thickness(2, 0, 12, 8) : new Thickness(12, 0, 12, 8);

        // Phone: the drawer covers the title bar too, over a dimmed page; wide: the title bar's search list covers the menu.
        Grid.SetRow(Menu, _narrow ? 0 : 1);
        Grid.SetRowSpan(Menu, _narrow ? 2 : 1);
        Menu.ZIndex = _narrow ? 2 : 0;
        TitleBar.ZIndex = _narrow ? 0 : 1;
        Scrim.ZIndex = 1;
        SlideDrawer(_narrow && menu.IsDrawerOpen);

        TitleBar.Apply(_narrow, IsMainWindow, ShowWindowButtons, withMenu ? menu.ReservedWidth : 0, _strip.ActiveTitle, _strip.Count);
    }

    private void SlideDrawer(bool open)
    {
        if (!_narrow)
        {
            _drawerShift.Transitions = null;
            _drawerShift.X = 0;
            Scrim.IsVisible = false;
            Scrim.Opacity = 0;
            return;
        }
        var duration = open ? DrawerOpen : DrawerClose;
        Easing easing = open ? new CubicEaseOut() : new CubicEaseIn();
        _drawerShift.Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = duration, Easing = easing }];
        _drawerShift.X = open ? 0 : -(_shell!.Menu.OpenWidth + 40); // the shadow too
        Scrim.IsVisible = open;
        Scrim.Opacity = open ? 1 : 0;
    }

    /// <summary>
    /// The page's own background (its core:Backdrop) spans this whole window, lined up with the shell's, so page and
    /// shell are one surface.
    /// </summary>
    private void AlignPageBackdrop()
    {
        if (PageHost.TranslatePoint(default, Root) is not { } at) return;
        var canvas = new Rect(-at.X, -at.Y, Root.Bounds.Width, Root.Bounds.Height);
        if (canvas == _canvas) return;
        _canvas = canvas;
        Backdrop.SetCanvas(PageHost, canvas);
    }

    /// <summary>The browser's keys: Ctrl+Tab / Ctrl+Shift+Tab switch tabs, Ctrl+W / Ctrl+F4 close one, Ctrl+F searches.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_strip is null || e.KeyModifiers is not (KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Tab:
                _strip.Strip.SelectNext(shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.W or Key.F4 when !shift && _strip.Strip.Active is { } active:
                _strip.Set.Close(active);
                e.Handled = true;
                break;
            case Key.F when !shift && IsMainWindow && !_narrow:
                TitleBar.FocusSearch();
                e.Handled = true;
                break;
        }
    }
}
