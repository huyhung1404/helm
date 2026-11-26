using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Helm.Core.Settings;
using Wpf.Ui.Controls;

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
///
/// The pane's width is animated here rather than by the template (which eases both ways the same): it opens fast then
/// slows down and closes slow then fast, and its background and shadow stay until it has finished closing, so the
/// labels never slide over the page without a background. The collapsed strip hides its scroll bar (the wheel still
/// scrolls); a fade at the bottom says more items are below.
/// </summary>
internal sealed class NavPaneController
{
    public const double CompactWidth = 48;
    public const double MinWidth = 220;
    public const double MaxWidth = 520;
    private const double PaneGridHorizontalMargin = 8; // PaneGrid has Margin="4,0,4,0" in the WPF-UI template
    private static readonly TimeSpan OpenAfter = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CloseAfter = TimeSpan.FromMilliseconds(400);
    private static readonly Duration OpenDuration = TimeSpan.FromMilliseconds(200);
    private static readonly Duration CloseDuration = TimeSpan.FromMilliseconds(160);
    private const double FadeHeight = 36;

    private readonly NavigationView _nav;
    private readonly Grid _host;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly FrameworkElement? _hoverTrigger;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Thumb _resizer;
    private readonly NavigationViewItem _pinItem;
    private readonly SymbolIcon _pinIcon = new();
    private Grid? _paneGrid;
    private FrameworkElement? _content;
    private ScrollViewer? _scroller;
    private bool _pinned;
    private bool _expanded;
    private bool _closing; // collapsing: the background stays until the width animation ends
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

        // A footer item (last) instead of a pane header: no extra row above the menu, and it stays visible (as an
        // icon) in the collapsed strip.
        _pinItem = new NavigationViewItem { Icon = _pinIcon };
        _pinItem.Click += (_, _) => SetPinned(!_pinned);

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
            if (!_nav.FooterMenuItems.Contains(_pinItem)) _nav.FooterMenuItems.Add(_pinItem);
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
        _scroller = _nav.Template?.FindName("PART_ScrollViewer", _nav) as ScrollViewer;
        if (_scroller is not null) _scroller.ScrollChanged += (_, _) => UpdateFade();
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
        var changed = expanded != _expanded;
        _expanded = expanded;
        _nav.IsPaneOpen = expanded; // starts the template's own width animation, replaced right below
        if (changed) AnimateWidth(expanded);
        UpdateLayout();
    }

    /// <summary>Open: fast, then slowing down; close: slow, then fast (and shorter). The open width then follows the
    /// pane length again (resizing), the closed one holds the strip width.</summary>
    private void AnimateWidth(bool open)
    {
        if (_paneGrid is null) return;
        _closing = !open && !_pinned;
        var animation = new DoubleAnimation(open ? _width : CompactWidth - PaneGridHorizontalMargin, open ? OpenDuration : CloseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn },
            FillBehavior = open ? FillBehavior.Stop : FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            if (_expanded != open) return; // turned around halfway: the newer animation finishes the job
            _closing = false;
            UpdateLayout();
        };
        _paneGrid.BeginAnimation(FrameworkElement.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
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
        _pinItem.Content = _pinned ? "Unpin menu" : "Pin menu";
        _pinItem.ToolTip = _pinned ? "Let the menu hide automatically" : "Keep the menu open";
        if (_paneGrid is null || _content is null) return;

        var expandedWidth = _width + PaneGridHorizontalMargin;
        var reserved = _pinned ? expandedWidth : CompactWidth; // compact PaneGrid is CompactPaneLength - 8 wide, incl. margins = 48
        _content.Margin = new Thickness(reserved, 0, 0, 0);

        if ((_expanded || _closing) && !_pinned)
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

        // The icon strip is too narrow for a scroll bar; the wheel still scrolls it.
        if (_scroller is not null)
            _scroller.VerticalScrollBarVisibility = _expanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        UpdateFade();
    }

    /// <summary>Fades the bottom of the item list while more items are below it.</summary>
    private void UpdateFade()
    {
        if (_scroller is null) return;
        var more = _scroller.VerticalOffset + _scroller.ViewportHeight < _scroller.ExtentHeight - 0.5;
        if (!more || _scroller.ActualHeight <= FadeHeight)
        {
            _scroller.OpacityMask = null;
            return;
        }
        var start = 1 - FadeHeight / _scroller.ActualHeight;
        _scroller.OpacityMask = new LinearGradientBrush(
            new GradientStopCollection { new(Colors.Black, 0), new(Colors.Black, start), new(Colors.Transparent, 1) },
            new Point(0, 0), new Point(0, 1));
    }
}
