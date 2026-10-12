using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Ui;

namespace Helm.Modules.ShortcutGuide;

/// <summary>
/// The guide on screen: a panel over the app in front that never takes the focus and lets clicks through, so the
/// shortcut you are about to press still reaches the app. The shortcuts flow into as many columns as the monitor
/// needs (at most four). Reused between holds.
/// </summary>
internal sealed class GuideOverlayWindow : Window
{
    private const double ShadowSpace = 20, ColumnWidth = 300, RowHeight = 30, ColumnGap = 28;
    private const int MaxColumns = 4;

    private readonly IWindowService _windows;
    private readonly Border _surface;
    private MonitorInfo? _monitor;
    private GuidePosition _position;

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
            Padding = new Thickness(24, 18, 24, 18),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.4, Direction = 270 },
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        _surface.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _surface.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        Content = _surface;

        SourceInitialized += (_, _) => _windows.MakePassiveOverlay(new WindowInteropHelper(this).Handle);
    }

    public void ShowGuide(GuideView view, MonitorInfo? monitor, GuidePosition position)
    {
        _monitor = monitor;
        _position = position;
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
        root.Children.Add(BuildHeader(view));

        if (view.IsEmpty)
        {
            root.Children.Add(Secondary($"Nothing in {view.AppName} starts with {string.Join("+", view.HeldCaps)}.", new Thickness(0, 4, 0, 0)));
        }
        else
        {
            var columns = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var column in Layout(view))
            {
                var panel = new StackPanel { Width = ColumnWidth, Margin = new Thickness(0, 0, ColumnGap, 0) };
                // The descriptions of a column line up after its longest keys.
                Grid.SetIsSharedSizeScope(panel, true);
                foreach (var row in column) panel.Children.Add(row);
                columns.Children.Add(panel);
            }
            // The last column needs no gap after it.
            if (columns.Children.Count > 0) ((FrameworkElement)columns.Children[^1]).Margin = new Thickness(0);
            root.Children.Add(columns);
        }

        var footer = BuildFooter(view);
        if (footer is not null) root.Children.Add(footer);
        return root;
    }

    private static FrameworkElement BuildHeader(GuideView view)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        var brand = Secondary("Shortcut Guide", new Thickness(24, 0, 0, 0));
        brand.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(brand, Dock.Right);
        header.Children.Add(brand);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new Keycaps { Keys = view.HeldCaps, IsAccent = true });
        var name = new TextBlock
        {
            Text = view.AppName,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        left.Children.Add(name);
        header.Children.Add(left);
        return header;
    }

    private static FrameworkElement? BuildFooter(GuideView view)
    {
        var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (view.Hints.Count > 0)
        {
            footer.Children.Add(Secondary("Also hold", new Thickness(0, 0, 8, 0), center: true));
            foreach (var hint in view.Hints)
            {
                footer.Children.Add(new Keycaps { Keys = hint.Caps });
                footer.Children.Add(Secondary($"{hint.Count} more", new Thickness(0, 0, 16, 0), center: true));
            }
        }
        if (!view.IsKnownApp)
        {
            footer.Children.Add(Secondary($"No shortcuts saved for {view.AppName} yet: add them in Helm → Shortcut Guide.",
                new Thickness(0, view.Hints.Count > 0 ? 6 : 0, 0, 0), center: true));
        }
        return footer.Children.Count > 0 ? footer : null;
    }

    /// <summary>Splits the sections into columns that fit the monitor, keeping a section title above its first line.</summary>
    private List<List<FrameworkElement>> Layout(GuideView view)
    {
        var scale = _monitor?.Scale ?? 1;
        var workHeight = (_monitor?.WorkArea.Height ?? SystemParameters.WorkArea.Height * scale) / scale;
        var workWidth = (_monitor?.WorkArea.Width ?? SystemParameters.WorkArea.Width * scale) / scale;
        // Room for the header, the footer and some of the app around the panel.
        var rowsPerColumn = Math.Max(6, (int)((workHeight * 0.85 - 150) / RowHeight));
        var maxColumns = Math.Clamp((int)((workWidth * 0.94 - 2 * ShadowSpace - 48 + ColumnGap) / (ColumnWidth + ColumnGap)), 1, MaxColumns);

        var rows = new List<(bool IsTitle, FrameworkElement Element)>();
        foreach (var section in view.Sections)
        {
            rows.Add((true, SectionTitle(section.Title, rows.Count == 0)));
            foreach (var item in section.Items) rows.Add((false, ItemRow(item)));
        }

        var capacity = rowsPerColumn * maxColumns;
        if (rows.Count > capacity)
        {
            var hidden = rows.Skip(capacity - 1).Count(r => !r.IsTitle);
            rows = rows.Take(capacity - 1).ToList();
            if (rows[^1].IsTitle) rows.RemoveAt(rows.Count - 1);
            rows.Add((false, Secondary($"…and {hidden} more", new Thickness(0, 6, 0, 0))));
        }

        var columnCount = Math.Clamp((int)Math.Ceiling(rows.Count / (double)rowsPerColumn), 1, maxColumns);
        var perColumn = (int)Math.Ceiling(rows.Count / (double)columnCount);
        var columns = new List<List<FrameworkElement>> { new() };
        for (var i = 0; i < rows.Count; i++)
        {
            var current = columns[^1];
            var full = current.Count >= perColumn;
            // A section title never ends a column: it moves to the next one with its lines.
            var orphanTitle = rows[i].IsTitle && current.Count >= perColumn - 1 && current.Count > 0;
            if ((full || orphanTitle) && columns.Count < columnCount) columns.Add(current = new List<FrameworkElement>());
            if (rows[i].IsTitle && current.Count == 0) rows[i].Element.Margin = new Thickness(0, 0, 0, 4);
            current.Add(rows[i].Element);
        }
        return columns;
    }

    private static FrameworkElement SectionTitle(string title, bool first)
    {
        var text = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, first ? 0 : 12, 0, 4),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        return text;
    }

    private static FrameworkElement ItemRow(GuideItem item)
    {
        var grid = new Grid { Height = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 40, SharedSizeGroup = "Keys" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < item.Keys.Count; i++)
        {
            if (i > 0) keys.Children.Add(Secondary("then", new Thickness(0, 0, 4, 0), center: true, size: 11));
            keys.Children.Add(new Keycaps { Keys = item.Keys[i] });
        }
        grid.Children.Add(keys);

        var does = new TextBlock
        {
            Text = item.Does,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Does,
        };
        Grid.SetColumn(does, 1);
        grid.Children.Add(does);
        return grid;
    }

    private static TextBlock Secondary(string text, Thickness margin, bool center = false, double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, Margin = margin, TextWrapping = TextWrapping.Wrap };
        if (center) block.VerticalAlignment = VerticalAlignment.Center;
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    private void Place()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        if (_monitor is not { } monitor)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = _position == GuidePosition.Bottom ? area.Bottom - ActualHeight : area.Top + (area.Height - ActualHeight) / 2;
            return;
        }
        var scale = monitor.Scale;
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scale);
        var work = monitor.WorkArea;
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        var y = _position == GuidePosition.Bottom
            ? work.Bottom - height - (int)Math.Round(12 * scale) // the shadow space already keeps it off the edge
            : work.Top + Math.Max(0, (work.Height - height) / 2);
        _windows.SetWindowPosition(hwnd, new PixelPoint(x, Math.Max(work.Top, y)));
    }
}
