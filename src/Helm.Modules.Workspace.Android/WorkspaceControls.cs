using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Helm.Modules.Workspace;

/// <summary>The colours of the control screen: VS Code's dark theme, whatever Helm's own theme is.</summary>
internal static class Vs
{
    public static readonly IBrush Bg = Brush("#1f1f1f");
    public static readonly IBrush Bar = Brush("#181818");
    public static readonly IBrush Raise = Brush("#252526");
    public static readonly IBrush Raise2 = Brush("#2d2d2d");
    public static readonly IBrush Line = Brush("#333333");
    public static readonly IBrush Line2 = Brush("#454545");
    public static readonly IBrush Fg = Brush("#cccccc");
    public static readonly IBrush FgStrong = Brush("#e7e7e7");
    public static readonly IBrush Muted = Brush("#8b8b8b");
    public static readonly IBrush Faint = Brush("#6a6a6a");
    public static readonly IBrush Accent = Brush("#d97757");
    public static readonly IBrush Link = Brush("#4daafc");
    public static readonly IBrush Bad = Brush("#f14c4c");
    public static readonly IBrush CodeBg = Brush("#181818");
    public static readonly IBrush InlineCode = Brush("#d7ba7d");
    public static readonly FontFamily Mono = new("monospace");

    private static ImmutableSolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}

/// <summary>Markdown drawn with native controls: headings, lists, code, tables, quotes; selectable text.</summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownBlock>?>(nameof(Blocks));

    static MarkdownView() => BlocksProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Build());

    public MarkdownView() => Spacing = 6;

    public IReadOnlyList<MarkdownBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    private void Build()
    {
        Children.Clear();
        foreach (var block in Blocks ?? []) Children.Add(Build(block));
    }

    private static Control Build(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => Text(block.Spans, block.Level <= 2 ? 17 : 15, bold: true, strong: true, margin: new Thickness(0, 8, 0, 0)),
        MarkdownBlockKind.Bullet or MarkdownBlockKind.Numbered => ListItem(block),
        MarkdownBlockKind.Code => CodeBlock(block.Code),
        MarkdownBlockKind.Table => Table(block.Rows),
        MarkdownBlockKind.Quote => new Border
        {
            BorderBrush = Vs.Line2,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Child = Text(block.Spans, foreground: Vs.Muted),
        },
        MarkdownBlockKind.Rule => new Border { Height = 1, Background = Vs.Line, Margin = new Thickness(0, 6) },
        _ => Text(block.Spans),
    };

    private static Control ListItem(MarkdownBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(4 + block.Level * 16, 0, 0, 0) };
        var marker = new TextBlock
        {
            Text = block.Kind == MarkdownBlockKind.Bullet ? "•" : block.Marker,
            Foreground = Vs.Muted,
            FontSize = 14,
            MinWidth = block.Kind == MarkdownBlockKind.Bullet ? 14 : 22,
        };
        var text = Text(block.Spans);
        Grid.SetColumn(text, 1);
        grid.Children.Add(marker);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>One block of text with its styled runs.</summary>
    internal static TextBlock Text(IReadOnlyList<MarkdownSpan> spans, double size = 14, bool bold = false, bool strong = false,
        IBrush? foreground = null, Thickness margin = default)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            LineHeight = size * 1.45,
            Foreground = foreground ?? (strong ? Vs.FgStrong : Vs.Fg),
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Margin = margin,
        };
        var inlines = new InlineCollection();
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.Bold) { run.FontWeight = FontWeight.SemiBold; run.Foreground = Vs.FgStrong; }
            if (span.Italic) run.FontStyle = FontStyle.Italic;
            if (span.Code)
            {
                run.FontFamily = Vs.Mono;
                run.FontSize = size - 2;
                run.Foreground = Vs.InlineCode;
                run.Background = Vs.Raise2;
            }
            if (span.Link) run.Foreground = Vs.Link;
            inlines.Add(run);
        }
        block.Inlines = inlines;
        return block;
    }

    internal static Control CodeBlock(string code) => new Border
    {
        Background = Vs.CodeBg,
        BorderBrush = Vs.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = code,
                FontFamily = Vs.Mono,
                FontSize = 12,
                LineHeight = 18,
                Foreground = Vs.Fg,
                Margin = new Thickness(10, 8),
            },
        },
    };

    private static Control Table(IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownSpan>>> rows)
    {
        var columns = rows.Max(r => r.Count);
        var grid = new Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var spans = c < rows[r].Count ? rows[r][c] : [];
                var cell = new Border
                {
                    BorderBrush = Vs.Line2,
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Background = r == 0 ? Vs.Raise : null,
                    Padding = new Thickness(8, 5),
                    Child = Text(spans, 13, bold: r == 0, strong: r == 0),
                };
                ((TextBlock)cell.Child).MaxWidth = 260;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }
}

/// <summary>How much of the context window is used, as a small ring (VS Code's context indicator).</summary>
public sealed class ContextRing : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<ContextRing, double>(nameof(Fraction));

    static ContextRing() => AffectsRender<ContextRing>(FractionProperty);

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 2;
        context.DrawEllipse(null, new Pen(Vs.Line2, 2.5), center, radius, radius);
        var f = Math.Clamp(Fraction, 0, 1);
        if (f <= 0) return;
        var pen = new Pen(f > 0.85 ? Vs.Bad : Vs.Accent, 2.5, lineCap: PenLineCap.Round);
        if (f >= 0.999)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = f * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>
/// The edges of a tab strip that scrolls sideways: an arrow where tabs are hidden, a red pill with a count when hidden
/// tabs want you (<see cref="IAttentionItem"/>). Tapping an edge brings the nearest such tab into sight, or scrolls a
/// page when none does. A mouse wheel turns into sideways scrolling.
/// </summary>
internal sealed class EdgeHints
{
    private const double Edge = 30;
    private readonly ScrollViewer _scroller;
    private readonly ItemsControl _items;
    private readonly Button _left;
    private readonly Button _right;

    public EdgeHints(ScrollViewer scroller, ItemsControl items, Button left, Button right)
    {
        _scroller = scroller;
        _items = items;
        _left = left;
        _right = right;
        scroller.ScrollChanged += (_, _) => Update();
        scroller.SizeChanged += (_, _) => Update();
        left.Click += (_, _) => Go(-1);
        right.Click += (_, _) => Go(1);
        scroller.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (Math.Abs(e.Delta.Y) <= Math.Abs(e.Delta.X)) return;
            scroller.Offset = new Vector(Math.Clamp(scroller.Offset.X - e.Delta.Y * 50, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)), 0);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The tabs as (left, width, attention), in strip coordinates.</summary>
    private List<(double X, double Width, int Attention, Control Tab)> Tabs()
    {
        var tabs = new List<(double, double, int, Control)>();
        for (var i = 0; i < _items.ItemCount; i++)
        {
            if (_items.ContainerFromIndex(i) is not { } c || c.TranslatePoint(default, _items) is not { } p) continue;
            tabs.Add((p.X, c.Bounds.Width, (c.DataContext as IAttentionItem)?.AttentionCount ?? 0, c));
        }
        return tabs;
    }

    public void Update()
    {
        var x = _scroller.Offset.X;
        var view = _scroller.Viewport.Width;
        var extent = _scroller.Extent.Width;
        var tabs = Tabs();
        Set(_left, x > 2, tabs.Where(t => t.X < x + Edge).Sum(t => t.Attention));
        Set(_right, x + view < extent - 2, tabs.Where(t => t.X + t.Width > x + view - Edge).Sum(t => t.Attention));
    }

    private static void Set(Button edge, bool more, int hot)
    {
        edge.IsVisible = more;
        edge.Classes.Set("hot", more && hot > 0);
        if ((edge.Content as Border)?.Child is Panel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } count)
        {
            count.Text = hot.ToString();
            count.IsVisible = more && hot > 0;
        }
        Avalonia.Automation.AutomationProperties.SetName(edge, hot > 0 ? $"{hot} hidden {(hot == 1 ? "tab needs" : "tabs need")} you" : "More tabs");
    }

    /// <summary>The selected tab stays in sight.</summary>
    public void KeepSelectedInView()
    {
        foreach (var (left, width, _, tab) in Tabs())
        {
            if (tab.DataContext is not (ProjectTab { IsSelected: true } or SessionTab { IsSelected: true })) continue;
            var x = _scroller.Offset.X;
            if (left < x + Edge) _scroller.Offset = new Vector(Math.Max(0, left - Edge), 0);
            else if (left + width > x + _scroller.Viewport.Width - Edge) _scroller.Offset = new Vector(left + width - _scroller.Viewport.Width + Edge, 0);
        }
        Update();
    }

    private void Go(int side)
    {
        var x = _scroller.Offset.X;
        var view = _scroller.Viewport.Width;
        var hidden = Tabs().Where(t => t.Attention > 0 && (side < 0 ? t.X < x + Edge : t.X + t.Width > x + view - Edge)).ToList();
        double to;
        Control? target = null;
        if (hidden.Count > 0)
        {
            var t = side < 0 ? hidden[^1] : hidden[0];
            target = t.Tab;
            to = side < 0 ? t.X - Edge : t.X + t.Width - view + Edge;
        }
        else to = x + side * view * 0.7;
        _scroller.Offset = new Vector(Math.Clamp(to, 0, Math.Max(0, _scroller.Extent.Width - view)), 0);
        if (target is not null) Flash(target);
    }

    /// <summary>The tab the edge led to lights up for a moment, so the eye finds it.</summary>
    private static void Flash(Control tab)
    {
        tab.Classes.Add("flash");
        Avalonia.Threading.DispatcherTimer.RunOnce(() => tab.Classes.Remove("flash"), TimeSpan.FromMilliseconds(900));
    }
}
