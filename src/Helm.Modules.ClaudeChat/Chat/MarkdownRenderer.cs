using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdCell = Markdig.Extensions.Tables.TableCell;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfCell = System.Windows.Documents.TableCell;
using WpfRow = System.Windows.Documents.TableRow;
using WpfTable = System.Windows.Documents.Table;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// Turns assistant markdown into a themed <see cref="FlowDocument"/>: headings, paragraphs, lists (with task
/// boxes), code blocks, quotes, tables, rules and links. Colours are resource references, so the document follows
/// the light/dark theme. Links are clickable only for http, https and mailto. Must run on a UI (STA) thread.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    private static readonly FontFamily s_mono = new("Cascadia Mono, Consolas, Courier New");

    public static FlowDocument Render(string markdown, double fontSize = 14)
    {
        var document = new FlowDocument
        {
            FontSize = fontSize,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        document.FontFamily = SystemFonts.MessageFontFamily;

        var parsed = Markdig.Markdown.Parse(markdown ?? string.Empty, s_pipeline);
        foreach (var block in parsed) AddBlock(document.Blocks, block, fontSize);
        TrimOuterMargins(document.Blocks);
        return document;
    }

    /// <summary>True for links a click may open: web pages and mail, never file:, javascript: or custom schemes.</summary>
    public static bool IsSafeLink(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https" or "mailto")) return false;
        uri = parsed;
        return true;
    }

    // ---- blocks -----------------------------------------------------------------------------------------------------

    private static void AddBlock(BlockCollection target, MdBlock block, double fontSize)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var p = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
                p.FontSize = heading.Level switch { 1 => fontSize + 6, 2 => fontSize + 4, 3 => fontSize + 2, _ => fontSize };
                AddInlines(p.Inlines, heading.Inline);
                target.Add(p);
                break;
            }
            case ParagraphBlock paragraph:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(p.Inlines, paragraph.Inline);
                target.Add(p);
                break;
            }
            case ListBlock list:
                target.Add(RenderList(list, fontSize));
                break;
            case FencedCodeBlock or CodeBlock:
                target.Add(RenderCode((LeafBlock)block));
                break;
            case QuoteBlock quote:
            {
                var section = new Section
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(10, 0, 0, 0),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                };
                section.SetResourceReference(WpfBlock.BorderBrushProperty, "AccentFillColorDefaultBrush");
                section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                foreach (var child in quote) AddBlock(section.Blocks, child, fontSize);
                TrimOuterMargins(section.Blocks);
                target.Add(section);
                break;
            }
            case ThematicBreakBlock:
            {
                var rule = new Paragraph { Margin = new Thickness(0, 4, 0, 12), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2 };
                rule.SetResourceReference(WpfBlock.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                target.Add(rule);
                break;
            }
            case MdTable table:
                target.Add(RenderTable(table, fontSize));
                break;
            case HtmlBlock html:
                target.Add(new Paragraph(new Run(LinesOf(html))) { Margin = new Thickness(0, 0, 0, 8) });
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(target, child, fontSize);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(p.Inlines, leaf.Inline);
                target.Add(p);
                break;
            }
        }
    }

    private static List RenderList(ListBlock list, double fontSize)
    {
        var wpfList = new List
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(22, 0, 0, 0),
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        };
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) wpfList.StartIndex = start;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            foreach (var child in item) AddBlock(listItem.Blocks, child, fontSize);
            foreach (var p in listItem.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0, 0, 0, 2);
            if (IsTaskItem(item)) wpfList.MarkerStyle = TextMarkerStyle.None;
            wpfList.ListItems.Add(listItem);
        }
        return wpfList;
    }

    private static bool IsTaskItem(ListItemBlock item) =>
        item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList };

    private static Section RenderCode(LeafBlock code)
    {
        var text = LinesOf(code).TrimEnd('\n', '\r');
        var p = new Paragraph(new Run(text))
        {
            FontFamily = s_mono,
            FontSize = 12.5,
            Margin = new Thickness(0),
        };
        var section = new Section(p)
        {
            Margin = new Thickness(0, 2, 0, 10),
            Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(1),
        };
        section.SetResourceReference(WpfBlock.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        section.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorSecondaryBrush");
        return section;
    }

    private static WpfTable RenderTable(MdTable table, double fontSize)
    {
        var wpfTable = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1, 1, 0, 0) };
        wpfTable.SetResourceReference(WpfBlock.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var columns = table.OfType<MdRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++) wpfTable.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdRow>())
        {
            var wpfRow = new WpfRow();
            if (row.IsHeader) wpfRow.FontWeight = FontWeights.SemiBold;
            foreach (var cell in row.OfType<MdCell>())
            {
                var wpfCell = new WpfCell { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                wpfCell.SetResourceReference(WpfCell.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                foreach (var child in cell) AddBlock(wpfCell.Blocks, child, fontSize);
                TrimOuterMargins(wpfCell.Blocks);
                wpfRow.Cells.Add(wpfCell);
            }
            group.Rows.Add(wpfRow);
        }
        wpfTable.RowGroups.Add(group);
        return wpfTable;
    }

    // ---- inlines ----------------------------------------------------------------------------------------------------

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container) target.Add(RenderInline(inline));
    }

    private static WpfInline RenderInline(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());

            case EmphasisInline emphasis:
            {
                var span = new Span();
                AddInlines(span.Inlines, emphasis);
                if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                else span.FontStyle = FontStyles.Italic;
                return span;
            }

            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = s_mono, FontSize = 12.5 };
                run.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorSecondaryBrush");
                return run;
            }

            case LinkInline { IsImage: true } image:
                return new Run($"[image: {PlainText(image)}]");

            case LinkInline link:
            {
                var span = new Span();
                AddInlines(span.Inlines, link);
                if (span.Inlines.Count == 0) span.Inlines.Add(new Run(link.Url ?? string.Empty));
                return MakeLink(span, link.Url);
            }

            case AutolinkInline auto:
                return MakeLink(new Span(new Run(auto.Url)), auto.IsEmail ? "mailto:" + auto.Url : auto.Url);

            case LineBreakInline:
                // Chat replies use single newlines on purpose (one fact per line), so soft breaks stay breaks.
                return new LineBreak();

            case HtmlInline html:
                return new Run(html.Tag);

            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());

            case TaskList task:
                return new Run(task.Checked ? "☑" : "☐"); // the literal after it keeps its leading space

            case ContainerInline container:
            {
                var span = new Span();
                AddInlines(span.Inlines, container);
                return span;
            }

            default:
                return new Run(inline.ToString());
        }
    }

    /// <summary>A clickable link for safe schemes; anything else stays plain (underlined) text.</summary>
    private static WpfInline MakeLink(Span content, string? url)
    {
        if (!IsSafeLink(url, out var uri))
        {
            content.TextDecorations = TextDecorations.Underline;
            return content;
        }
        var link = new Hyperlink(content) { NavigateUri = uri, ToolTip = uri!.ToString() };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        return link;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static string LinesOf(LeafBlock block) => block.Lines.ToString();

    private static string PlainText(ContainerInline container) =>
        string.Concat(container.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

    /// <summary>No gap above the first block or below the last one; the chat row spacing handles that.</summary>
    private static void TrimOuterMargins(BlockCollection blocks)
    {
        if (blocks.FirstBlock is { } first) first.Margin = first.Margin with { Top = 0 };
        if (blocks.LastBlock is { } last) last.Margin = last.Margin with { Bottom = 0 };
    }
}
