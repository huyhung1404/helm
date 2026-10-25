using System.Security.Cryptography;
using System.Text;
using Helm.Core.Sync;

namespace Helm.Modules.NovelReader;

public static class NovelReaderIds
{
    public const string ModuleId = "novel-reader";

    public const string DisplayName = "Novel Reader";

    public const string Description = "Read Chinese novels in Vietnamese: VietPhrase conversion, the names you fix per novel, and reading aloud. Your novels sync privately.";
}

/// <summary>
/// A novel in the library, synced: its text is an encrypted blob (or several) uploaded before the record, so another
/// device never sees a novel it cannot open. Its text changes only when chapters are added from its web source.
/// </summary>
public sealed record NovelBook
{
    public const int MaxTitleLength = 200;

    public string Title { get; init; } = "";

    /// <summary>The name of the file it came from.</summary>
    public string FileName { get; init; } = "";

    /// <summary>The text as UTF-8, whatever the file was encoded in (a novel added from a file).</summary>
    public BlobRef? Blob { get; init; }

    /// <summary>
    /// The text in parts of about a hundred chapters (a novel from the web), read one after another; used instead of
    /// <see cref="Blob"/>. Adding chapters stores and syncs only the parts that changed. Versions before 0.54 do not
    /// know it and show such a novel empty.
    /// </summary>
    public IReadOnlyList<BookPart> Parts { get; init; } = [];

    public long Size { get; init; }

    /// <summary>A picture for the library (a JPEG, at most 600 px), synced like the text; null shows a drawn cover.</summary>
    public BlobRef? Cover { get; init; }

    /// <summary>Counted when it was added, so the library shows "12 / 249" before the text is downloaded.</summary>
    public int ChapterCount { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public string AddedFrom { get; init; } = "";

    /// <summary>When names were last looked for in this novel (null: never, so opening it looks for them).</summary>
    public DateTimeOffset? NamesScannedAt { get; init; }

    /// <summary>Names the scan found that the user deleted: a later scan does not add them again.</summary>
    public IReadOnlyList<string> IgnoredNames { get; init; } = [];

    /// <summary>The web page listing its chapters (scraped from there, or set by the user): "Continue download" reads it again.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Helm looks at <see cref="SourceUrl"/> for new chapters by itself (on the PC, a few times a day).</summary>
    public bool AutoCheck { get; init; }

    /// <summary>New chapters Helm finds by itself are downloaded and saved at once, without a preview.</summary>
    public bool AutoSave { get; init; }

    /// <summary>
    /// A device without a browser (the phone) asked for the new chapters at this time: the PC downloads and saves them
    /// when it sees the request, then sets <see cref="SourceCheckedAt"/> past it.
    /// </summary>
    public DateTimeOffset? UpdateRequestedAt { get; init; }

    /// <summary>When the source was last looked at, and how many chapters it had that this novel does not.</summary>
    public DateTimeOffset? SourceCheckedAt { get; init; }

    public int SourceNewChapters { get; init; }
}

/// <summary>One part of a novel's text: the blob, a hash of its text (an unchanged part is kept), and its chapters.</summary>
public sealed record BookPart(BlobRef Blob, string Hash, int Chapters);

public enum EntryKind
{
    /// <summary>A name: wins over every phrase around it and keeps its capitals.</summary>
    Name,
    /// <summary>A meaning for a phrase.</summary>
    Phrase,
}

/// <summary>
/// A name or meaning the user saved, for one novel (<see cref="BookId"/>) or for all (null). One record per entry, so
/// two devices adding names at the same time both keep theirs. The Chinese stays in the encrypted body; the record id
/// only carries a hash of it (<see cref="EntryIds"/>).
/// </summary>
public sealed record NovelEntry
{
    public string? BookId { get; init; }

    public EntryKind Kind { get; init; }

    public string Chinese { get; init; } = "";

    public string Vietnamese { get; init; } = "";

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Added by the name scan, not typed by the user; editing it makes it the user's.</summary>
    public bool Auto { get; init; }
}

/// <summary>Where reading stopped in one novel, to the sentence; the device that read last wins.</summary>
public sealed record NovelProgress
{
    public int Chapter { get; init; }

    /// <summary>The paragraph in the chapter (0 is the chapter title).</summary>
    public int Paragraph { get; init; }

    /// <summary>The sentence in the paragraph reading aloud stopped at (0 in records written before it existed).</summary>
    public int Sentence { get; init; }

    /// <summary>Paragraphs in that chapter, so the library can say "paragraph 30 / 58" without opening the novel.</summary>
    public int ParagraphCount { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string Device { get; init; } = "";
}

/// <summary>
/// A dictionary file (VietPhrase.txt, Names.txt…), synced as an encrypted blob so a new device or a reinstall gets the
/// same dictionaries without downloading them again, and a source that disappears from the web is not a loss.
/// </summary>
public sealed record NovelDictionary
{
    public string Name { get; init; } = "";

    /// <summary>Bytes of the file itself (the blob is smaller when <see cref="Compressed"/>).</summary>
    public long Size { get; init; }

    public BlobRef? Blob { get; init; }

    /// <summary>The blob holds the file gzipped (about a third of VietPhrase's size to download); older records hold it as is.</summary>
    public bool Compressed { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedFrom { get; init; } = "";
}

/// <summary>Record ids of saved entries. The sync server sees ids in the clear, so they hold a hash, not the Chinese.</summary>
public static class EntryIds
{
    public const string AllBooks = "all";

    public static string For(string? bookId, EntryKind kind, string chinese)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chinese));
        return $"{bookId ?? AllBooks}:{(kind == EntryKind.Name ? 'n' : 'p')}:{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
    }
}
