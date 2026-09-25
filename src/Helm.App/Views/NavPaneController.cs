using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Helm.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace Helm.App.Views;

/// <summary>
/// Auto-hiding, pinnable, resizable navigation pane on top of WPF-UI's NavigationView (pure view glue).
///
/// Unpinned, the pane is a strip of icons; hovering the strip (or the title-bar icon) slides the full pane out
/// <em>over</em> the content, and it collapses again shortly after the mouse leaves. Pinned, it stays open and the
/// content sits beside it. The right edge can be dragged to change the width. State lives in general.json.
///
/// How: NavigationView's template is a 2-column grid (pane | content). The content border is made to span both
/// columns with a left margin equal to the reserved pane width, so expanding the pane never reflows the page.
/// Hover is tracked by polling the cursor position (template-internal elements raise noisy Enter/Leave pairs).
/// </summary>
internal sealed class NavPaneController
{
    public const double CompactWidth = 48;
    public const double MinWidth = 220;
    public const double MaxWidth = 520;
    private const double PaneGridHorizontalMargin = 8; // PaneGrid has Margin="4,0,4,0" in the WPF-UI template
    private static readonly TimeSpan OpenAfter = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CloseAfter = TimeSpan.FromMilliseconds(400);

    private readonly NavigationView _nav;
    private readonly Grid _host;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly FrameworkElement? _hoverTrigger;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Thumb _resizer;
    private readonly Button _pinButton;
    private readonly SymbolIcon _pinIcon = new();
    private Grid? _paneGrid;
    private FrameworkElement? _content;
    private bool _pinned;
    private bool _expanded;
    private double _width;
    private DateTime _hoverSince = DateTime.MinValue;
    private DateTime _awaySince = DateTime.MinValue;

    public NavPaneController(NavigationView nav, Grid host, ISettingsStore<GeneralSettings> general, FrameworkElement? hoverTrigger)
    {
        _nav = nav;
        _host = host;
        _general = general;
        _hoverTrigger = hoverTrigger;
        _pinned = general.Current.Window.NavPinned;
        _width = Math.Clamp(double.IsFinite(general.Current.Window.NavWidth) ? general.Current.Window.NavWidth : 300, MinWidth, MaxWidth);

        _nav.CompactPaneLength = CompactWidth;
        _nav.OpenPaneLength = _width;
        _nav.IsPaneToggleVisible = false;

        _pinButton = new Button
        {
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 4),
            Content = _pinIcon,
        };
        _pinButton.Click += (_, _) => SetPinned(!_pinned);
        _nav.PaneHeader = _pinButton;

        _resizer = new Thumb
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.SizeWE,
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            ToolTip = "Drag to resize the menu",
        };
        Grid.SetRow(_resizer, Grid.GetRow(nav));
        Panel.SetZIndex(_resizer, 100);
        _resizer.DragDelta += OnResizeDelta;
        _resizer.DragCompleted += (_, _) => _general.Update(s => s.Window.NavWidth = _width);
        _host.Children.Add(_resizer);

        _poll.Tick += (_, _) => Poll();
        _nav.Loaded += (_, _) =>
        {
            AttachTemplate();
            SetExpanded(_pinned);
            _poll.Start();
        };
        _nav.Unloaded += (_, _) => _poll.Stop();
    }

    private void AttachTemplate()
    {
        if (_paneGrid is not null) return;
        _nav.ApplyTemplate();
        _paneGrid = _nav.Template?.FindName("PaneGrid", _nav) as Grid;
        var root = VisualTreeHelper.GetChildrenCount(_nav) > 0 ? VisualTreeHelper.GetChild(_nav, 0) as Grid : null;
        _content = root?.Children.OfType<FrameworkElement>().FirstOrDefault(e => Grid.GetColumn(e) == 1);
        if (_paneGrid is null || _content is null)
        {
            Serilog.Log.Warning("NavigationView template changed; auto-hide menu disabled");
            return;
        }

        Grid.SetColumn(_content, 0);
        Grid.SetColumnSpan(_content, 2);
        Panel.SetZIndex(_paneGrid, 10);
    }

    /// <summary>Opens after the cursor rests on the pane/title icon for a moment; closes after it has been away a moment.</summary>
    private void Poll()
    {
        if (_pinned || _paneGrid is null) return;
        var window = Window.GetWindow(_nav);
        if (window is null || !window.IsActive && !window.IsMouseOver)
        {
            if (_expanded && !_resizer.IsDragging) SetExpanded(false);
            return;
        }

        var now = DateTime.UtcNow;
        if (IsCursorOverPane())
        {
            _awaySince = DateTime.MinValue;
            if (_hoverSince == DateTime.MinValue) _hoverSince = now;
            if (!_expanded && now - _hoverSince >= OpenAfter) SetExpanded(true);
        }
        else
        {
            _hoverSince = DateTime.MinValue;
            if (_awaySince == DateTime.MinValue) _awaySince = now;
            if (_expanded && now - _awaySince >= CloseAfter && !_resizer.IsDragging) SetExpanded(false);
        }
    }

    private bool IsCursorOverPane()
    {
        if (_resizer.IsDragging) return true;
        var p = Mouse.GetPosition(_nav);
        var paneWidth = _expanded ? _width + PaneGridHorizontalMargin + _resizer.Width / 2 : CompactWidth;
        if (p.X >= 0 && p.X <= paneWidth && p.Y >= 0 && p.Y <= _nav.ActualHeight) return true;
        if (_hoverTrigger is { IsVisible: true } trigger)
        {
            var t = Mouse.GetPosition(trigger);
            if (t.X >= 0 && t.Y >= 0 && t.X <= trigger.ActualWidth && t.Y <= trigger.ActualHeight) return true;
        }
        return false;
    }

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        _general.Update(s => s.Window.NavPinned = pinned);
        SetExpanded(pinned || _expanded);
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _nav.IsPaneOpen = expanded;
        if (expanded)
        {
            // Compact mode collapses groups; show their children again when the pane opens.
            foreach (var item in _nav.MenuItems.OfType<NavigationViewItem>().Where(i => i.MenuItems.Count > 0))
                item.IsExpanded = true;
        }
        UpdateLayout();
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        _width = Math.Clamp(_width + e.HorizontalChange, MinWidth, MaxWidth);
        _nav.OpenPaneLength = _width;
        UpdateLayout();
    }

    /// <summary>Reserves room for the pane (pinned) or just the icon strip (auto-hide) and styles the overlay.</summary>
    private void UpdateLayout()
    {
        _pinIcon.Symbol = _pinned ? SymbolRegular.PinOff24 : SymbolRegular.Pin24;
        _pinButton.ToolTip = _pinned ? "Unpin the menu (auto-hide)" : "Pin the menu open";
        _pinButton.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        if (_paneGrid is null || _content is null) return;

        var expandedWidth = _width + PaneGridHorizontalMargin;
        var reserved = _pinned ? expandedWidth : CompactWidth; // compact PaneGrid is CompactPaneLength - 8 wide, incl. margins = 48
        _content.Margin = new Thickness(reserved, 0, 0, 0);

        if (_expanded && !_pinned)
        {
            _paneGrid.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
            _paneGrid.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black };
        }
        else
        {
            _paneGrid.Background = Brushes.Transparent;
            _paneGrid.Effect = null;
        }

        _resizer.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _resizer.Margin = new Thickness(expandedWidth - _resizer.Width / 2, 0, 0, 0);

    }
}
