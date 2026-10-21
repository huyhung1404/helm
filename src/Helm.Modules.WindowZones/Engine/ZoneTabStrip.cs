using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WindowZones.Engine;

/// <summary>
/// The tab strip on the UI thread: shown at the top of the zone when you switch windows or open the zone, gone a
/// moment after the last switch. It never takes the focus and clicks go through it.
/// </summary>
public sealed class ZoneTabStrip(IUiDispatcher ui, IWindowService windows, ILogger<ZoneTabStrip> logger) : IZoneTabStrip
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromMilliseconds(1300);

    private TabStripWindow? _window;
    private DispatcherTimer? _timer;

    public void Show(TabStripView view) => ui.Post(() =>
    {
        try
        {
            _window ??= new TabStripWindow(windows);
            _window.ShowStrip(view);
            _timer ??= new DispatcherTimer(ShowFor, DispatcherPriority.Normal, (_, _) => HideNow(), Dispatcher.CurrentDispatcher);
            _timer.Stop();
            _timer.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not show the zone's tabs");
        }
    });

    public void Hide() => ui.Post(HideNow);

    private void HideNow()
    {
        _timer?.Stop();
        _window?.Hide();
    }
}

/// <summary>A see-through strip of tabs, one per window of the zone: its program's icon and its title.</summary>
internal sealed class TabStripWindow : Window
{
    private const double TabWidth = 220, ShadowSpace = 12;

    private readonly IWindowService _windows;
    private readonly Border _surface;
    private TabStripView? _view;

    public TabStripWindow(IWindowService windows)
    {
        _windows = windows;
        Title = "Window Zones tabs";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        _surface = new Border
        {
            Margin = new Thickness(ShadowSpace),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.3, Direction = 270 },
        };
        _surface.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _surface.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        _surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        Content = _surface;

        SourceInitialized += (_, _) => _windows.MakePassiveOverlay(new WindowInteropHelper(this).Handle);
    }

    public void ShowStrip(TabStripView view)
    {
        _view = view;
        _surface.Child = Build(view);
        if (!IsVisible)
        {
            // Off-screen first, so it never flashes at the old spot before moving.
            Left = -32000;
            Top = -32000;
            Show();
        }
        Place();
        // The strip may have moved to a monitor with another DPI and changed size: place it again once laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
    }

    private static UIElement Build(TabStripView view)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock
        {
            Text = view.Label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(label);

        // Never wider than the zone: the tabs share what is left, each at most TabWidth.
        var room = view.Zone.Width / view.Monitor.Scale - 2 * ShadowSpace - 120;
        var width = Math.Clamp(room / Math.Max(1, view.Windows.Count), 90, TabWidth);
        foreach (var w in view.Windows) row.Children.Add(Tab(w, w.Handle == view.Current, width));
        return row;
    }

    private static FrameworkElement Tab(ZoneWindowSnapshot window, bool current, double width)
    {
        var content = new DockPanel { LastChildFill = true };
        if (!string.IsNullOrEmpty(window.ProcessPath) && ShellIcons.ForFile(window.ProcessPath) is { } icon)
        {
            var image = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(image, Dock.Left);
            content.Children.Add(image);
        }
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (current) title.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        else if (window.IsMinimized) title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        content.Children.Add(title);

        var tab = new Border
        {
            Width = width,
            Height = 34,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Child = content,
        };
        tab.SetResourceReference(Border.BackgroundProperty, current ? "AccentFillColorDefaultBrush" : "ControlFillColorDefaultBrush");
        return tab;
    }

    /// <summary>Centred on the zone's top edge, just inside it (over the title bar of the window in front).</summary>
    private void Place()
    {
        if (!IsVisible || _view is not { } view) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var scale = view.Monitor.Scale;
        var width = (int)Math.Round(ActualWidth * scale);
        var zone = view.Zone;
        var x = zone.Left + Math.Max(0, (zone.Width - width) / 2);
        var y = zone.Top + (int)Math.Round(4 * scale);
        _windows.SetWindowPosition(hwnd, new PixelPoint(x, y));
    }
}
