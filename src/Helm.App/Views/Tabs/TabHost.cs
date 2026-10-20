using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Helm.App.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Helm.App.Views.Tabs;

/// <summary>
/// A window's tabs: the strip on top (tabs, then "+" to open another page) and the shown tab's page below it.
///
/// Dragging a tab along the strip reorders it; dragging it up or down off the strip takes it out into a new window
/// that follows the mouse (<see cref="TabManager.TearOff"/>), and dropping that window on another strip puts the tab
/// there. The strip holds the mouse while a tab is dragged, so tabs can move around under the cursor.
/// </summary>
internal sealed class TabHost : UserControl
{
    /// <summary>Where the strip's tabs start inside the host (DIPs): a torn-off tab is placed under the cursor by it.</summary>
    public const double StripLeft = 8;
    public const double StripTop = 6;

    /// <summary>How far off the strip (DIPs) a dragged tab must go before it leaves for a window of its own.</summary>
    private const double TearDistance = 24;

    private readonly List<ToolTab> _tabs = [];
    private readonly Grid _strip;
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _panel;
    private readonly Border _content;
    private ToolTab? _active;
    private Window? _window;

    private ToolTab? _pressed;
    private Point _pressedAt;
    private Point _grab;
    private bool _dragging;

    public TabHost(TabManager manager)
    {
        Manager = manager;
        Focusable = false;

        _panel = new StackPanel { Orientation = Orientation.Horizontal };
        _scroller = new ScrollViewer
        {
            Content = _panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
        };
        // A wheel over the strip scrolls the tabs sideways.
        _scroller.PreviewMouseWheel += (_, e) =>
        {
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };

        var add = new Button
        {
            Appearance = ControlAppearance.Transparent,
            Icon = new SymbolIcon(SymbolRegular.Add20),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = "Open a tool in a new tab",
            ContextMenu = new ContextMenu(),
        };
        System.Windows.Automation.AutomationProperties.SetName(add, "New tab");
        add.Click += (_, _) =>
        {
            add.ContextMenu = BuildLaunchMenu();
            add.ContextMenu.PlacementTarget = add;
            add.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            add.ContextMenu.IsOpen = true;
        };

        _strip = new Grid { Margin = new Thickness(StripLeft, StripTop, 8, 4), Background = Brushes.Transparent };
        _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _strip.Children.Add(_scroller);
        Grid.SetColumn(add, 1);
        _strip.Children.Add(add);
        _strip.MouseMove += OnStripMouseMove;
        _strip.MouseLeftButtonUp += (_, _) => EndDrag();
        _strip.LostMouseCapture += (_, _) => EndDrag();

        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");

        _content = new Border { ClipToBounds = true };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(divider, 1);
        Grid.SetRow(_content, 2);
        root.Children.Add(_strip);
        root.Children.Add(divider);
        root.Children.Add(_content);
        Content = root;

        Loaded += (_, _) =>
        {
            if (_window is not null) return;
            _window = Window.GetWindow(this);
            if (_window is not null) _window.Activated += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        };
    }

    public TabManager Manager { get; }

    public IReadOnlyList<ToolTab> Tabs => _tabs;

    public ToolTab? ActiveTab => _active;

    /// <summary>A tab was added, removed, moved or shown.</summary>
    public event EventHandler? Changed;

    /// <summary>The user is working in this host's window (it became the foreground window, or a tab was clicked).</summary>
    public event EventHandler? Activated;

    public void Insert(ToolTab tab, int index)
    {
        index = Math.Clamp(index, 0, _tabs.Count);
        tab.Host = this;
        _tabs.Insert(index, tab);
        if (tab.Header.Parent is Panel old) old.Children.Remove(tab.Header);
        _panel.Children.Insert(index, tab.Header);
        tab.Header.PreviewMouseDown += OnHeaderMouseDown;
        tab.Header.SetActive(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the tab off this window; the next one (or the previous) is shown if it was the shown one.</summary>
    public void Remove(ToolTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        if (ReferenceEquals(_pressed, tab)) EndDrag();
        _tabs.RemoveAt(index);
        _panel.Children.Remove(tab.Header);
        tab.Header.PreviewMouseDown -= OnHeaderMouseDown;
        tab.Host = null;
        if (ReferenceEquals(_active, tab))
        {
            _active = null;
            _content.Child = null;
            if (_tabs.Count > 0) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MoveTo(ToolTab tab, int index)
    {
        var from = _tabs.IndexOf(tab);
        index = Math.Clamp(index, 0, _tabs.Count - 1);
        if (from < 0 || from == index) return;
        _tabs.RemoveAt(from);
        _tabs.Insert(index, tab);
        _panel.Children.Remove(tab.Header);
        _panel.Children.Insert(index, tab.Header);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Activate(ToolTab tab)
    {
        if (ReferenceEquals(_active, tab) || !_tabs.Contains(tab)) return;
        _active?.Header.SetActive(false);
        _active = tab;
        tab.Header.SetActive(true);
        var presenter = tab.Presenter;
        if (presenter.Parent is Border holder) holder.Child = null;
        _content.Child = presenter;
        tab.Header.BringIntoView();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the next or previous tab, wrapping around.</summary>
    public void SelectNext(int step)
    {
        if (_tabs.Count < 2 || _active is null) return;
        var index = (_tabs.IndexOf(_active) + step + _tabs.Count) % _tabs.Count;
        Activate(_tabs[index]);
    }

    /// <summary>Whether a screen point (pixels) is over this window's strip, give or take a little.</summary>
    public bool StripContains(Point screen)
    {
        if (!_strip.IsVisible || PresentationSource.FromVisual(_strip) is null) return false;
        if (Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return false;
        var p = _strip.PointFromScreen(screen);
        return p.X >= 0 && p.X <= _strip.ActualWidth && p.Y >= -8 && p.Y <= _strip.ActualHeight + 8;
    }

    /// <summary>Where a tab dropped at a screen point (pixels) goes: before the first tab whose middle is right of it.</summary>
    public int IndexAt(Point screen) => PresentationSource.FromVisual(_panel) is null ? _tabs.Count : IndexAtX(_panel.PointFromScreen(screen).X, null);

    private int IndexAtX(double x, ToolTab? except)
    {
        var index = 0;
        foreach (var tab in _tabs)
        {
            if (ReferenceEquals(tab, except)) continue;
            var left = tab.Header.TranslatePoint(default, _panel).X;
            if (left + tab.Header.ActualWidth / 2 < x) index++;
        }
        return index;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabHeader header || _tabs.FirstOrDefault(t => ReferenceEquals(t.Header, header)) is not { } tab) return;
        Activated?.Invoke(this, EventArgs.Empty);
        if (e.ChangedButton == MouseButton.Middle)
        {
            Manager.Close(tab);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left || header.IsOnCloseButton(e.OriginalSource)) return;

        Activate(tab);
        _pressed = tab;
        _pressedAt = e.GetPosition(_panel);
        _grab = e.GetPosition(header);
        _dragging = false;
        // The strip holds the mouse, so the header can move under the cursor while it is dragged.
        _strip.CaptureMouse();
        e.Handled = true;
    }

    private void OnStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } tab || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_panel);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            // The only tab of a window that is not the main one: dragging it drags the window (onto a strip, maybe).
            if (_tabs.Count == 1 && Window.GetWindow(this) is TabWindow single)
            {
                EndDrag();
                Manager.DragWindow(single);
                return;
            }
        }

        var y = e.GetPosition(_strip).Y;
        if (y < -TearDistance || y > _strip.ActualHeight + TearDistance)
        {
            // The main window keeps at least one tab.
            if (_tabs.Count < 2) return;
            var grab = _grab;
            EndDrag();
            Manager.TearOff(tab, grab);
            return;
        }

        MoveTo(tab, IndexAtX(p.X, tab));
    }

    private void EndDrag()
    {
        _pressed = null;
        _dragging = false;
        if (_strip.IsMouseCaptured) _strip.ReleaseMouseCapture();
    }

    private ContextMenu BuildLaunchMenu()
    {
        var menu = new ContextMenu();
        foreach (var (look, pageType) in Manager.Launchable())
        {
            var icon = look.Icon();
            if (icon is SymbolIcon symbol) symbol.FontSize = 16;
            var item = new MenuItem { Header = look.Title, Icon = icon };
            item.Click += (_, _) => Manager.Open(pageType, this);
            menu.Items.Add(item);
        }
        return menu;
    }
}
