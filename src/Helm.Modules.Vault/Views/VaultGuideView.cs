using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Helm.Modules.Vault.Guide;

namespace Helm.Modules.Vault.Views;

/// <summary>
/// The user guide (docs/vault-guide.md, see <see cref="VaultGuide"/>) drawn with WPF controls. Links to a section
/// scroll to it. Colors are resource references, so the page follows the light/dark theme.
/// </summary>
public sealed class VaultGuideView : UserControl
{
    private static readonly FontFamily s_mono = new("Cascadia Mono, Consolas, Courier New");
    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.Ordinal);
    private readonly StackPanel _panel;
    private readonly ScrollViewer _scroll;

    public VaultGuideView() : this(VaultGuide.Blocks) { }

    public VaultGuideView(IReadOnlyList<GuideBlock> blocks)
    {
        _panel = new StackPanel { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 16, 32) };
        foreach (var block in blocks) _panel.Children.Add(Block(block));
        _scroll = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _scroll;
        // Inherited by every text block, so the guide is readable wherever it is hosted.
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    /// <summary>Scrolls so the heading with this anchor is at the top. False when there is no such heading.</summary>
    public bool ScrollTo(string anchor)
    {
        if (!_anchors.TryGetValue(anchor, out var target)) return false;
        _scroll.UpdateLayout();
        var top = target.TransformToAncestor(_panel).Transform(new Point(0, 0)).Y;
        _scroll.ScrollToVerticalOffset(Math.Max(0, top - 8));
        return true;
    }

    private FrameworkElement Block(GuideBlock block) => block switch
    {
        GuideHeading h => Heading(h),
        GuideParagraph p => Text(p.Spans, new Thickness(0, 0, 0, 10)),
        GuideList l => List(l),
        GuideTable t => Table(t),
        GuideCode c => Code(c),
        GuideNote n => Note(n),
        _ => new FrameworkElement(),
    };

    private TextBlock Heading(GuideHeading heading)
    {
        var text = Text(heading.Spans, heading.Level switch
        {
            1 => new Thickness(0, 0, 0, 12),
            2 => new Thickness(0, 24, 0, 8),
            _ => new Thickness(0, 16, 0, 6),
        });
        text.FontSize = heading.Level switch { 1 => 26, 2 => 20, _ => 16 };
        text.FontWeight = FontWeights.SemiBold;
        _anchors[heading.Anchor] = text;
        return text;
    }

    private TextBlock Text(IReadOnlyList<GuideSpan> spans, Thickness margin)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = 21 };
        foreach (var span in spans) text.Inlines.Add(Inline(span));
        return text;
    }

    private Inline Inline(GuideSpan span)
    {
        var run = new Run(span.Text);
        if (span.Bold) run.FontWeight = FontWeights.SemiBold;
        if (span.Code)
        {
            run.FontFamily = s_mono;
            run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        }
        if (span.Anchor is not { } anchor) return run;
        var link = new Hyperlink(run) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        link.Click += (_, _) => ScrollTo(anchor);
        return link;
    }

    private Grid List(GuideList list)
    {
        var grid = new Grid { Margin = new Thickness(4, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var marker = new TextBlock
            {
                Text = item.Checkbox switch { true => "☑", false => "☐", _ => list.Ordered ? $"{i + 1}." : "•" },
                FontWeight = list.Ordered ? FontWeights.SemiBold : FontWeights.Normal,
            };
            marker.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            var content = new StackPanel();
            foreach (var block in item.Blocks)
            {
                var element = Block(block);
                // A list item's paragraph sits tighter than a free paragraph.
                if (element is TextBlock { } paragraph && block is GuideParagraph) paragraph.Margin = new Thickness(0, 0, 0, 4);
                content.Children.Add(element);
            }
            Grid.SetRow(marker, i);
            Grid.SetRow(content, i);
            Grid.SetColumn(content, 1);
            grid.Children.Add(marker);
            grid.Children.Add(content);
        }
        return grid;
    }

    private Border Table(GuideTable table)
    {
        var columns = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        var grid = new Grid();
        // Columns share the width by how much text they hold, so long answers get the room.
        for (var c = 0; c < columns; c++)
        {
            var length = table.Rows.Select(r => c < r.Count ? string.Concat(r[c].Select(s => s.Text)).Length : 0).DefaultIfEmpty(0).Average();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(length, 12, 90), GridUnitType.Star) });
        }
        var rows = table.Header.Count > 0 ? new[] { table.Header }.Concat(table.Rows).ToList() : table.Rows.ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = r == 0 && table.Header.Count > 0;
            for (var c = 0; c < columns; c++)
            {
                var text = Text(c < rows[r].Count ? rows[r][c] : [], new Thickness(0));
                if (header) text.FontWeight = FontWeights.SemiBold;
                var cell = new Border { Padding = new Thickness(10, 7, 10, 7), BorderThickness = new Thickness(0, 0, 0, r == rows.Count - 1 ? 0 : 1), Child = text };
                cell.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                if (header) cell.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 12), Child = grid, ClipToBounds = true };
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        return frame;
    }

    private static Border Code(GuideCode code)
    {
        var text = new TextBlock { Text = code.Text, FontFamily = s_mono, TextWrapping = TextWrapping.Wrap };
        var frame = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 2, 0, 12), Child = text };
        frame.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return frame;
    }

    private Border Note(GuideNote note)
    {
        var content = new StackPanel();
        foreach (var block in note.Blocks) content.Children.Add(Block(block));
        if (content.Children.Count > 0 && content.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 2, 0, 12),
            Child = content,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "SystemFillColorCautionBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        return frame;
    }
}
