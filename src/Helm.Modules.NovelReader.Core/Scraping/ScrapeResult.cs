namespace Helm.Modules.NovelReader.Scraping;

/// <summary>What became of one chapter of the list.</summary>
public enum ScrapeOutcome
{
    /// <summary>Its text came.</summary>
    Got,

    /// <summary>The novel has it already (continuing a download): not fetched.</summary>
    Skipped,

    /// <summary>No text came after a few tries.</summary>
    Failed,

    /// <summary>The site wants a login or payment for it.</summary>
    Locked,
}

/// <summary>One chapter of the list, with its text when it came.</summary>
public sealed record ScrapeItem(WebLink Link, ScrapeOutcome Outcome, ScrapedChapter? Chapter)
{
    /// <summary>The name to show: the chapter's title when it came, else the link's text.</summary>
    public string Title => Chapter?.Title ?? Link.Text;
}

/// <summary>
/// Everything one scrape gives: the book's name, where its list is, every chapter of the list in order, and the cover
/// picture the site shows (a small JPEG once the platform has shrunk it), or null.
/// </summary>
public sealed record ScrapeResult(string Title, string SourceUrl, IReadOnlyList<ScrapeItem> Items, byte[]? Cover = null)
{
    public int Count(ScrapeOutcome outcome) => Items.Count(i => i.Outcome == outcome);
}
