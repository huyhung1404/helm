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
/// A novel in the library, synced: its text is an encrypted blob uploaded before the record, so another device never
/// sees a novel it cannot open. Read-only: the text never changes after it was added.
/// </summary>
public sealed record NovelBook
{
    public const int MaxTitleLength = 200;

    public string Title { get; init; } = "";

    /// <summary>The name of the file it came from.</summary>
    public string FileName { get; init; } = "";

    /// <summary>The text as UTF-8, whatever the file was encoded in.</summary>
    public BlobRef? Blob { get; init; }

    public long Size { get; init; }

    /// <summary>A picture for the library (a JPEG, at most 600 px), synced like the text; null shows a drawn cover.</summary>
    public BlobRef? Cover { get; init; }

    /// <summary>Counted when it was added, so the library shows "12 / 249" before the text is downloaded.</summary>
    public int ChapterCount { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public string AddedFrom { get; init; } = "";
}

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

    public long Size { get; init; }

    public BlobRef? Blob { get; init; }

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
