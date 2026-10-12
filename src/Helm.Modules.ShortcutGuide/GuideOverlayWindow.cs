using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Helm.Core.Desktop;
using Helm.Core.Geometry;

namespace Helm.Modules.ShortcutGuide;

/// <summary>How the guide looks: where it sits, how much of the app shows through, how many rows of keys it has.</summary>
public sealed record GuideLook(GuidePosition Position, double BackgroundOpacity, int Rows)
{
    public static GuideLook From(ShortcutGuideSettings settings) => new(
        settings.Position,
        Math.Clamp(settings.BackgroundOpacity, ShortcutGuideSettings.MinOpacity, ShortcutGuideSettings.MaxOpacity) / 100.0,
        Math.Clamp(settings.Rows, ShortcutGuideSettings.MinRows, ShortcutGuideSettings.MaxRows));
}

/// <summary>
/// The guide on screen: a see-through strip that never takes the focus and lets clicks through, so the shortcut you
/// are about to press still reaches the app. The held keys sit on the left with a "+", then a grid of keys, each with
/// what it does. Reused between holds.
/// </summary>
internal sealed class GuideOverlayWindow : Window
{
    private const double ShadowSpace = 16, CellWidth = 224, CellHeight = 42, KeyHeight = 32, HeldKeyHeight = 40;
    private const int MaxColumns = 6;

    private readonly IWindowService _windows;
    private readonly Border _surface;
    private MonitorInfo? _monitor;
    private GuideLook _look = new(GuidePosition.Bottom, 0.85, 3);

    public GuideOverlayWindow(IWindowService windows)
    {
        _windows = windows;
        Title = "Shortcut Guide";
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
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.3, Direction = 270 },
        };
        _surface.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _surface.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        Content = _surface;

        SourceInitialized += (_, _) => _windows.MakePassiveOverlay(new WindowInteropHelper(this).Handle);
    }

    public void ShowGuide(GuideView view, MonitorInfo? monitor, GuideLook look)
    {
        _monitor = monitor;
        _look = look;
        _surface.Background = SeeThrough("ApplicationBackgroundColor", look.BackgroundOpacity);
        _surface.Child = Build(view);
        if (!IsVisible)
        {
            // Off-screen first, so it never flashes at the old spot before moving.
            Left = -32000;
            Top = -32000;
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
        Place();
        // The window may have moved to a monitor with another DPI and changed size: place it again once laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
    }

    public void Update(GuideView view)
    {
        if (!IsVisible) return;
        _surface.Child = Build(view);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
    }

    public void HideGuide()
    {
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private UIElement Build(GuideView view)
    {
        var root = new StackPanel();
        root.Children.Add(BuildCaption(view));

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The held keys, then a big plus: "Ctrl +" reads like the start of every shortcut on the right.
        var held = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        for (var i = 0; i < view.HeldCaps.Count; i++)
        {
            if (i > 0) held.Children.Add(Plus(16));
            held.Children.Add(HeldKey(view.HeldCaps[i]));
        }
        held.Children.Add(Plus(22));
        body.Children.Add(held);

        var cells = BuildCells(view, out var columns);
        var grid = new WrapPanel { ItemWidth = CellWidth, ItemHeight = CellHeight, Width = columns * CellWidth };
        foreach (var cell in cells) grid.Children.Add(cell);
        Grid.SetColumn(grid, 1);
        body.Children.Add(grid);
        root.Children.Add(body);
        return root;
    }

    /// <summary>The app's name on the left, other modifiers worth holding on the right.</summary>
    private static FrameworkElement BuildCaption(GuideView view)
    {
        var caption = new DockPanel { Margin = new Thickness(2, 0, 2, 6), LastChildFill = true };
        if (view.Hints.Count > 0)
        {
            var hints = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0) };
            hints.Children.Add(Caption("Also hold", secondary: true));
            foreach (var hint in view.Hints)
                hints.Children.Add(Caption($"{string.Join("+", hint.Caps)} {hint.Count}", secondary: false, margin: new Thickness(8, 0, 0, 0)));
            DockPanel.SetDock(hints, Dock.Right);
            caption.Children.Add(hints);
        }
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Caption(view.AppName, secondary: false);
        title.FontWeight = FontWeights.SemiBold;
        name.Children.Add(title);
        if (!view.IsKnownApp) name.Children.Add(Caption(" · add its shortcuts in Helm → Shortcut Guide", secondary: true));
        caption.Children.Add(name);
        return caption;
    }

    /// <summary>One cell per shortcut, as many columns as the monitor allows and at most <see cref="GuideLook.Rows"/> rows.</summary>
    private List<FrameworkElement> BuildCells(GuideView view, out int columns)
    {
        var items = view.Sections.SelectMany(s => s.Items).ToList();
        var scale = _monitor?.Scale ?? 1;
        var workWidth = (_monitor?.WorkArea.Width ?? SystemParameters.WorkArea.Width * scale) / scale;
        // Three quarters of the screen at most, minus the held keys on the left.
        var fit = Math.Clamp((int)((workWidth * 0.75 - 2 * ShadowSpace - 28 - 160) / CellWidth), 2, MaxColumns);
        columns = Math.Max(1, Math.Min(fit, items.Count));
        var capacity = columns * _look.Rows;

        var cells = new List<FrameworkElement>();
        if (items.Count == 0)
        {
            cells.Add(Caption("Nothing here starts with these keys.", secondary: true));
            return cells;
        }
        var shown = items.Count > capacity ? capacity - 1 : items.Count;
        foreach (var item in items.Take(shown)) cells.Add(Cell(item));
        if (items.Count > shown)
        {
            var more = Caption($"+{items.Count - shown} more in Helm", secondary: true);
            more.VerticalAlignment = VerticalAlignment.Center;
            cells.Add(more);
        }
        return cells;
    }

    private static FrameworkElement Cell(GuideItem item)
    {
        var cell = new DockPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < item.Keys.Count; i++)
        {
            if (i > 0) keys.Children.Add(Caption("then", secondary: true, size: 11, margin: new Thickness(0, 0, 4, 0)));
            foreach (var cap in item.Keys[i]) keys.Children.Add(Key(cap));
        }
        DockPanel.SetDock(keys, Dock.Left);
        cell.Children.Add(keys);
        cell.Children.Add(new TextBlock
        {
            Text = item.Does,
            FontSize = 13,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return cell;
    }

    /// <summary>A keyboard key: a face with a darker lip below it.</summary>
    private static FrameworkElement Key(string label)
    {
        var face = new Border
        {
            MinWidth = KeyHeight,
            Height = KeyHeight - 3,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = KeyLabel(label, 13, "TextFillColorPrimaryBrush"),
        };
        face.SetResourceReference(Border.BackgroundProperty, "ControlSolidFillColorDefaultBrush");
        face.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
        return new Border
        {
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(6),
            Background = Lip("ControlSolidFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = face,
        };
    }

    /// <summary>A held key: bigger, in the accent colour.</summary>
    private static FrameworkElement HeldKey(string label)
    {
        var face = new Border
        {
            MinWidth = HeldKeyHeight + 8,
            Height = HeldKeyHeight - 4,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(7),
            Child = KeyLabel(label, 15, "TextOnAccentFillColorPrimaryBrush"),
        };
        face.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        return new Border
        {
            Padding = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(7),
            Background = Lip("AccentFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = face,
        };
    }

    private static UIElement KeyLabel(string label, double size, string brushKey)
    {
        if (label == "Win") return WindowsLogo(brushKey, size - 1);
        var text = new TextBlock
        {
            Text = label,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }

    private static UIElement WindowsLogo(string brushKey, double size)
    {
        var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var half = (size - 1) / 2;
        foreach (var (row, col) in new[] { (0, 0), (0, 1), (1, 0), (1, 1) })
        {
            var square = new Rectangle
            {
                Width = half,
                Height = half,
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = row == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            };
            square.SetResourceReference(Shape.FillProperty, brushKey);
            grid.Children.Add(square);
        }
        RenderOptions.SetEdgeMode(grid, EdgeMode.Aliased);
        return grid;
    }

    private static TextBlock Plus(double size)
    {
        var plus = new TextBlock
        {
            Text = "+",
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        plus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return plus;
    }

    private static TextBlock Caption(string text, bool secondary, double size = 12, Thickness margin = default)
    {
        var block = new TextBlock { Text = text, FontSize = size, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        return block;
    }

    /// <summary>The theme's background colour with <paramref name="opacity"/>, so the app shows through.</summary>
    private Brush SeeThrough(string colorKey, double opacity)
    {
        var color = TryFindResource(colorKey) is Color c ? c : Color.FromRgb(0x20, 0x20, 0x20);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>The lip under a key: its face colour, a third darker.</summary>
    private static Brush Lip(string faceKey)
    {
        var face = Application.Current?.TryFindResource(faceKey) is SolidColorBrush b ? b.Color : Color.FromRgb(0x45, 0x45, 0x45);
        var lip = new SolidColorBrush(Color.FromArgb(face.A, (byte)(face.R * 0.62), (byte)(face.G * 0.62), (byte)(face.B * 0.62)));
        lip.Freeze();
        return lip;
    }

    private void Place()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var bottom = _look.Position == GuidePosition.Bottom;
        if (_monitor is not { } monitor)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = bottom ? area.Bottom - ActualHeight - 8 : area.Top + (area.Height - ActualHeight) / 2;
            return;
        }
        var scale = monitor.Scale;
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scale);
        var work = monitor.WorkArea;
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        var y = bottom
            ? work.Bottom - height - (int)Math.Round(8 * scale) // the shadow space already keeps it off the taskbar
            : work.Top + Math.Max(0, (work.Height - height) / 2);
        _windows.SetWindowPosition(hwnd, new PixelPoint(x, Math.Max(work.Top, y)));
    }
}
