namespace Helm.Modules.NovelReader.Reports;

/// <summary>
/// A sentence the reader marked as translated wrong, synced: the Chinese, what Helm made of it then (the text and how
/// it was cut into dictionary pieces), the right translation and a note. Reports are the material for improving the
/// converter: they are exported as a document and read by an AI through MCP, and each one stays a test case after the
/// converter gets it right.
/// </summary>
public sealed record TranslationReport
{
    public string? BookId { get; init; }

    /// <summary>The novel's title when it was reported (kept if the novel is deleted later).</summary>
    public string BookTitle { get; init; } = "";

    /// <summary>The chapter, from 0.</summary>
    public int Chapter { get; init; }

    public string ChapterTitle { get; init; } = "";

    /// <summary>The sentence (or sentences) in the form the converter reads (<see cref="Text.ChineseText.NormalizeLine"/>).</summary>
    public string Chinese { get; init; } = "";

    /// <summary>What Helm showed for <see cref="Chinese"/> when it was reported.</summary>
    public string Helm { get; init; } = "";

    /// <summary>How Helm cut <see cref="Chinese"/>: each piece's Chinese, its Vietnamese and where it came from.</summary>
    public IReadOnlyList<ReportPiece> Pieces { get; init; } = [];

    /// <summary>The right translation, as the reader wrote it ("" when only the note says what is wrong).</summary>
    public string Correct { get; init; } = "";

    /// <summary>What is wrong, in the reader's words ("的 order", "道 is nói here").</summary>
    public string Note { get; init; } = "";

    /// <summary>The word the reader clicked to report it, often where it goes wrong.</summary>
    public string Word { get; init; } = "";

    /// <summary>The whole paragraph the sentence is in, for context.</summary>
    public string Paragraph { get; init; } = "";

    /// <summary>The translation an AI suggested (never written over <see cref="Correct"/>: the reader picks it).</summary>
    public string Suggestion { get; init; } = "";

    /// <summary>What the AI says Helm got wrong.</summary>
    public string SuggestionNote { get; init; } = "";

    /// <summary>The kind of error, one of <see cref="ErrorKinds.All"/> ("" while nobody said).</summary>
    public string ErrorKind { get; init; } = "";

    public DateTimeOffset? SuggestedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string Device { get; init; } = "";
}

/// <summary>One piece of Helm's translation: the Chinese, the Vietnamese shown, and its kind (phrase, name, Hán Việt…).</summary>
public sealed record ReportPiece(string Chinese, string Text, string Kind);

/// <summary>The kinds of translation errors, which say what part of the converter a report calls for.</summary>
public static class ErrorKinds
{
    /// <summary>的 phrases and modifiers in Chinese order ("hắn phụ thân của kiếm").</summary>
    public const string WordOrder = "word-order";

    /// <summary>在…上/里/中 frames read word by word ("tại cái bàn thượng").</summary>
    public const string Location = "location";

    /// <summary>A word with several meanings took the wrong one (道 as "đạo" where it is "nói").</summary>
    public const string Meaning = "meaning";

    /// <summary>The sentence was cut into the wrong words (研究生|命 instead of 研究|生命).</summary>
    public const string Segmentation = "segmentation";

    /// <summary>The wrong form of address for the characters (xưng hô).</summary>
    public const string Pronoun = "pronoun";

    /// <summary>A name not known, or cut apart.</summary>
    public const string Name = "name";

    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [WordOrder, Location, Meaning, Segmentation, Pronoun, Name, Other];

    /// <summary>A known kind as given, anything else as <see cref="Other"/>.</summary>
    public static string Normalize(string? kind) =>
        All.FirstOrDefault(k => string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Other;
}
