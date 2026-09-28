using Helm.Modules.Vault.Guide;

namespace Helm.Tests;

/// <summary>The in-app guide is docs/vault-guide.md, embedded and parsed; it must survive edits of that file.</summary>
public sealed class VaultGuideTests
{
    [Fact]
    public void The_embedded_guide_is_the_docs_file()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Helm.sln"))) root = root.Parent;
        var file = File.ReadAllText(Path.Combine(root!.FullName, "docs", "vault-guide.md"));
        Assert.Equal(file.ReplaceLineEndings(), VaultGuide.ReadEmbedded().ReplaceLineEndings());
    }

    [Fact]
    public void Every_link_inside_the_guide_leads_to_one_of_its_headings()
    {
        var blocks = VaultGuide.Blocks;
        var anchors = Flatten(blocks).OfType<GuideHeading>().Select(h => h.Anchor).ToHashSet();
        var links = Flatten(blocks).SelectMany(Spans).Where(s => s.Anchor is not null).Select(s => s.Anchor!).Distinct().ToList();

        Assert.NotEmpty(links);
        Assert.All(links, link => Assert.Contains(link, anchors));
    }

    [Fact]
    public void The_guide_has_what_the_app_draws()
    {
        var all = Flatten(VaultGuide.Blocks).ToList();
        Assert.Equal("Hướng dẫn dùng Vault", string.Concat(all.OfType<GuideHeading>().First(h => h.Level == 1).Spans.Select(s => s.Text)));
        Assert.Contains(all, b => b is GuideTable { Header.Count: 3, Rows.Count: > 2 });
        Assert.Contains(all, b => b is GuideCode c && c.Text.Contains("helm-vault-restore list", StringComparison.Ordinal));
        Assert.Contains(all, b => b is GuideNote);
        Assert.Contains(all.OfType<GuideList>().SelectMany(l => l.Items), i => i.Checkbox == false);
        // Bold and code runs keep their style; no raw Markdown is left in the text.
        var spans = all.SelectMany(Spans).ToList();
        Assert.Contains(spans, s => s.Bold);
        Assert.Contains(spans, s => s.Code);
        Assert.DoesNotContain(spans, s => s.Text.Contains("**", StringComparison.Ordinal) || s.Text.Contains("](", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Ba thứ bí mật", "ba-thứ-bí-mật")]
    [InlineData("Khi có sự cố", "khi-có-sự-cố")]
    [InlineData("Restore everything on a new device", "restore-everything-on-a-new-device")]
    [InlineData("Lock after inactivity (PC)", "lock-after-inactivity-pc")]
    public void Anchors_match_GitHub(string heading, string anchor) => Assert.Equal(anchor, VaultGuide.Slug(heading));

    private static IEnumerable<GuideBlock> Flatten(IEnumerable<GuideBlock> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var inner = block switch
            {
                GuideList l => l.Items.SelectMany(i => i.Blocks),
                GuideNote n => n.Blocks,
                _ => [],
            };
            foreach (var child in Flatten(inner)) yield return child;
        }
    }

    private static IEnumerable<GuideSpan> Spans(GuideBlock block) => block switch
    {
        GuideHeading h => h.Spans,
        GuideParagraph p => p.Spans,
        GuideTable t => t.Header.SelectMany(c => c).Concat(t.Rows.SelectMany(r => r.SelectMany(c => c))),
        _ => [],
    };
}
