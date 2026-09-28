using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Helm.Modules.Vault.Guide;

/// <summary>A run of text in the guide. <see cref="Anchor"/> is set for a link to a heading of the guide itself.</summary>
public sealed record GuideSpan(string Text, bool Bold = false, bool Code = false, string? Anchor = null);

/// <summary>A block of the guide. Each platform draws these with its own controls.</summary>
public abstract record GuideBlock;

public sealed record GuideHeading(int Level, IReadOnlyList<GuideSpan> Spans, string Anchor) : GuideBlock;

public sealed record GuideParagraph(IReadOnlyList<GuideSpan> Spans) : GuideBlock;

/// <summary>A list; each item is a list of blocks (a paragraph, and maybe a nested list). <see cref="Checkbox"/> marks task items.</summary>
public sealed record GuideList(bool Ordered, IReadOnlyList<GuideListItem> Items) : GuideBlock;

public sealed record GuideListItem(IReadOnlyList<GuideBlock> Blocks, bool? Checkbox = null);

/// <summary>A table: the header row, then the rows; each cell is a list of spans.</summary>
public sealed record GuideTable(IReadOnlyList<IReadOnlyList<GuideSpan>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<GuideSpan>>> Rows) : GuideBlock;

public sealed record GuideCode(string Text) : GuideBlock;

/// <summary>A highlighted note (a Markdown quote).</summary>
public sealed record GuideNote(IReadOnlyList<GuideBlock> Blocks) : GuideBlock;

/// <summary>
/// The Vault user guide, parsed from docs/vault-guide.md (embedded in this assembly), so GitHub and the app show the
/// same text. Links to other files become plain text; links to a heading of the guide keep their anchor.
/// </summary>
public static class VaultGuide
{
    private static readonly Lazy<IReadOnlyList<GuideBlock>> s_blocks = new(() => Parse(ReadEmbedded()));

    public static IReadOnlyList<GuideBlock> Blocks => s_blocks.Value;

    public static string ReadEmbedded()
    {
        using var stream = typeof(VaultGuide).Assembly.GetManifestResourceStream("Helm.Modules.Vault.vault-guide.md")
            ?? throw new InvalidOperationException("The vault guide is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<GuideBlock> Parse(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().Build();
        var document = Markdown.Parse(markdown, pipeline);
        return document.Select(Block).OfType<GuideBlock>().ToList();
    }

    /// <summary>GitHub's anchor for a heading: lower case, punctuation dropped, spaces to hyphens.</summary>
    public static string Slug(string heading)
    {
        var lower = heading.Trim().ToLowerInvariant();
        var kept = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') kept.Append(c);
            else if (c == ' ') kept.Append('-');
        }
        return kept.ToString();
    }

    private static GuideBlock? Block(Block block) => block switch
    {
        HeadingBlock h => Heading(h),
        ParagraphBlock p => new GuideParagraph(Spans(p.Inline)),
        ListBlock l => new GuideList(l.IsOrdered, l.OfType<ListItemBlock>().Select(Item).ToList()),
        Table t => TableBlock(t),
        FencedCodeBlock or CodeBlock => new GuideCode(CodeText((LeafBlock)block)),
        QuoteBlock q => new GuideNote(q.Select(Block).OfType<GuideBlock>().ToList()),
        _ => null,
    };

    private static GuideHeading Heading(HeadingBlock heading)
    {
        var spans = Spans(heading.Inline);
        return new GuideHeading(heading.Level, spans, Slug(string.Concat(spans.Select(s => s.Text))));
    }

    private static GuideListItem Item(ListItemBlock item)
    {
        var blocks = item.Select(Block).OfType<GuideBlock>().ToList();
        // A task item "- [ ] text": Markdig puts a TaskList inline first in the paragraph.
        bool? box = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList task } ? task.Checked : null;
        return new GuideListItem(blocks, box);
    }

    private static GuideTable TableBlock(Table table)
    {
        var rows = table.OfType<TableRow>().Select(r => (IReadOnlyList<IReadOnlyList<GuideSpan>>)r.OfType<TableCell>()
            .Select(c => (IReadOnlyList<GuideSpan>)c.OfType<ParagraphBlock>().SelectMany(p => Spans(p.Inline)).ToList()).ToList()).ToList();
        var header = table.OfType<TableRow>().FirstOrDefault()?.IsHeader == true ? rows[0] : [];
        return new GuideTable(header, header.Count > 0 ? rows.Skip(1).ToList() : rows);
    }

    private static string CodeText(LeafBlock code) =>
        string.Join("\n", code.Lines.Lines.Take(code.Lines.Count).Select(l => l.Slice.ToString())).TrimEnd();

    private static List<GuideSpan> Spans(ContainerInline? inline)
    {
        var spans = new List<GuideSpan>();
        if (inline is not null) AddSpans(inline, bold: false, anchor: null, spans);
        return Merge(spans);
    }

    private static void AddSpans(ContainerInline container, bool bold, string? anchor, List<GuideSpan> spans)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    spans.Add(new GuideSpan(literal.Content.ToString(), bold, Anchor: anchor));
                    break;
                case CodeInline code:
                    spans.Add(new GuideSpan(code.Content, bold, Code: true, Anchor: anchor));
                    break;
                case EmphasisInline emphasis:
                    AddSpans(emphasis, bold || emphasis.DelimiterCount >= 2, anchor, spans);
                    break;
                case LinkInline link:
                    AddSpans(link, bold, link.Url is { } url && url.StartsWith('#') ? url[1..] : anchor, spans);
                    break;
                case LineBreakInline:
                    spans.Add(new GuideSpan(" ", bold, Anchor: anchor));
                    break;
                case TaskList:
                    break;
                case ContainerInline other:
                    AddSpans(other, bold, anchor, spans);
                    break;
                case HtmlEntityInline entity:
                    spans.Add(new GuideSpan(entity.Transcoded.ToString(), bold, Anchor: anchor));
                    break;
            }
        }
    }

    /// <summary>Joins neighbours with the same style, so a paragraph is a few runs, not one per word.</summary>
    private static List<GuideSpan> Merge(List<GuideSpan> spans)
    {
        var merged = new List<GuideSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Bold == span.Bold && last.Code == span.Code && last.Anchor == span.Anchor)
                merged[^1] = last with { Text = last.Text + span.Text };
            else
                merged.Add(span);
        }
        return merged;
    }
}
