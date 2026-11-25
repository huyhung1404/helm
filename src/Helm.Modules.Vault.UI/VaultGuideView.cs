using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Helm.Modules.Vault.Guide;

namespace Helm.Modules.Vault.Ui;

/// <summary>
/// The user guide (docs/vault-guide.md, see <see cref="VaultGuide"/>) drawn with Avalonia controls, as a sheet over the
/// vault. A link to a section scrolls the sheet to it. Colours are theme resources, so it follows light and dark.
/// </summary>
public sealed class VaultGuideView : UserControl
{
    private static readonly FontFamily s_mono = new("Cascadia Mono, Consolas, monospace");
    private readonly Dictionary<string, Control> _anchors = new(StringComparer.Ordinal);
    private readonly StackPanel _panel;

    public VaultGuideView(Action close) : this(VaultGuide.Blocks, close) { }

    public VaultGuideView(IReadOnlyList<GuideBlock> blocks, Action close)
    {
        _panel = new StackPanel { Spacing = 0 };
        var closeButton = new Button
        {
            Classes = { "chip" },
            Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(closeButton, "Close");
        Avalonia.Automation.AutomationProperties.SetName(closeButton, "Close the guide");
        closeButton.Click += (_, _) => close();
        var title = new StackPanel
        {
            Children =
            {
                new TextBlock { Classes = { "eyebrow" }, Text = "VAULT" },
                new TextBlock { Text = "How the vault works", FontSize = 22, FontWeight = FontWeight.SemiBold },
            },
        };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(title);
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(closeButton);
        _panel.Children.Add(header);
        foreach (var block in blocks) _panel.Children.Add(Block(block));
        Content = _panel;
    }

    /// <summary>Scrolls so the heading with this anchor shows. False when there is no such heading.</summary>
    public bool ScrollTo(string anchor)
    {
        if (!_anchors.TryGetValue(anchor, out var target)) return false;
        target.BringIntoView();
        return true;
    }

    private Control Block(GuideBlock block) => block switch
    {
        GuideHeading h => Heading(h),
        GuideParagraph p => Text(p.Spans, new Thickness(0, 0, 0, 10)),
        GuideList l => List(l),
        GuideTable t => Table(t),
        GuideCode c => Code(c),
        GuideNote n => Note(n),
        _ => new Panel(),
    };

    private TextBlock Heading(GuideHeading heading)
    {
        var text = Text(heading.Spans, heading.Level switch
        {
            1 => new Thickness(0, 0, 0, 12),
            2 => new Thickness(0, 24, 0, 8),
            _ => new Thickness(0, 16, 0, 6),
        });
        text.FontSize = heading.Level switch { 1 => 24, 2 => 19, _ => 16 };
        text.FontWeight = FontWeight.SemiBold;
        _anchors[heading.Anchor] = text;
        return text;
    }

    private TextBlock Text(IReadOnlyList<GuideSpan> spans, Thickness margin)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = 21 };
        string? firstAnchor = null;
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeight.SemiBold;
            if (span.Code)
            {
                run.FontFamily = s_mono;
                run.Background = new SolidColorBrush(Colors.Gray, 0.18);
            }
            if (span.Anchor is { } anchor)
            {
                firstAnchor ??= anchor;
                run.TextDecorations = TextDecorations.Underline;
                run.Foreground = ThemeBrush("HelmEyebrow");
            }
            text.Inlines!.Add(run);
        }
        // A paragraph with a link to a section of the guide goes to that section when tapped.
        if (firstAnchor is { } target)
        {
            text.Cursor = new Cursor(StandardCursorType.Hand);
            text.PointerPressed += (_, _) => ScrollTo(target);
        }
        return text;
    }

    private static IBrush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var found) == true && found is IBrush brush ? brush : Brushes.Teal;

    private Grid List(GuideList list)
    {
        var grid = new Grid { Margin = new Thickness(4, 0, 0, 8), ColumnDefinitions = ColumnDefinitions.Parse("24,*") };
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var marker = new TextBlock
            {
                Text = item.Checkbox switch { true => "☑", false => "☐", _ => list.Ordered ? $"{i + 1}." : "•" },
                FontWeight = list.Ordered ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = ThemeBrush("HelmEyebrow"),
            };
            var content = new StackPanel();
            foreach (var block in item.Blocks)
            {
                var element = Block(block);
                // A list item's paragraph sits tighter than a free paragraph.
                if (element is TextBlock paragraph && block is GuideParagraph) paragraph.Margin = new Thickness(0, 0, 0, 4);
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
            grid.ColumnDefinitions.Add(new ColumnDefinition(Math.Clamp(length, 12, 90), GridUnitType.Star));
        }
        var rows = table.Header.Count > 0 ? new[] { table.Header }.Concat(table.Rows).ToList() : table.Rows.ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var header = r == 0 && table.Header.Count > 0;
            for (var c = 0; c < columns; c++)
            {
                var text = Text(c < rows[r].Count ? rows[r][c] : [], new Thickness(0));
                if (header) text.FontWeight = FontWeight.SemiBold;
                var cell = new Border
                {
                    Padding = new Thickness(10, 7),
                    BorderThickness = new Thickness(0, 0, 0, r == rows.Count - 1 ? 0 : 1),
                    BorderBrush = ThemeBrush("HelmGlassStroke"),
                    Background = header ? new SolidColorBrush(Colors.Gray, 0.12) : null,
                    Child = text,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("HelmGlassStroke"),
            Margin = new Thickness(0, 2, 0, 12),
            Child = grid,
            ClipToBounds = true,
        };
    }

    private static Border Code(GuideCode code) => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 10),
        Margin = new Thickness(0, 2, 0, 12),
        Background = new SolidColorBrush(Colors.Gray, 0.15),
        Child = new TextBlock { Text = code.Text, FontFamily = s_mono, TextWrapping = TextWrapping.Wrap },
    };

    private Border Note(GuideNote note)
    {
        var content = new StackPanel();
        foreach (var block in note.Blocks) content.Children.Add(Block(block));
        if (content.Children.Count > 0 && content.Children[^1] is Control last) last.Margin = new Thickness(0);
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = ThemeBrush("HelmEyebrow"),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 2, 0, 12),
            Background = ThemeBrush("HelmGlass"),
            Child = content,
        };
    }
}
