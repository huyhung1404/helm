using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Modules.NovelReader.Conversion;

namespace Helm.Modules.NovelReader;

/// <summary>A novel in the library: one card, a private "folder" with its text, names and progress.</summary>
public sealed partial class BookCardViewModel(string id) : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _names = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _titleDraft = "";

    /// <summary>The cover picture (JPEG bytes), or null for the drawn cover.</summary>
    [ObservableProperty] private byte[]? _cover;

    public string Id { get; } = id;

    /// <summary>Which stored picture <see cref="Cover"/> holds, so it is read again only when it changes.</summary>
    internal string? CoverBlobId { get; set; }

    public bool HasCover => Cover is not null;

    public bool HasNoCover => Cover is null;

    /// <summary>The drawn cover: one or two letters of the title on a gradient picked from the novel's id.</summary>
    public string Initials => CoverArt.Initials(Title);

    public string CoverColorStart => CoverArt.Colors(Id).Start;

    public string CoverColorEnd => CoverArt.Colors(Id).End;

    partial void OnCoverChanged(byte[]? value)
    {
        OnPropertyChanged(nameof(HasCover));
        OnPropertyChanged(nameof(HasNoCover));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Initials));

    public bool HasStatus => Status.Length > 0;

    public bool HasNames => Names.Length > 0;

    public bool IsNotRenaming => !IsRenaming;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnNamesChanged(string value) => OnPropertyChanged(nameof(HasNames));

    partial void OnIsRenamingChanged(bool value) => OnPropertyChanged(nameof(IsNotRenaming));

    // The web page the novel's chapters are listed on.
    [ObservableProperty] private bool _hasSource;
    [ObservableProperty] private bool _autoCheck;
    [ObservableProperty] private string _sourceStatus = "";
    [ObservableProperty] private bool _isEditingSource;
    [ObservableProperty] private string _sourceDraft = "";

    /// <summary>"Continue download" is running for this novel (its button shows that and waits).</summary>
    [ObservableProperty] private bool _isContinuing;

    [ObservableProperty] private bool _autoSave;

    /// <summary>A request for new chapters waits for the PC (shown on the phone, which cannot download them itself).</summary>
    [ObservableProperty] private bool _isUpdateRequested;

    public string AutoCheckText => AutoCheck ? "Stop checking for new chapters" : "Check for new chapters by itself";

    public string AutoSaveText => AutoSave ? "Show new chapters before saving them" : "Save new chapters by itself (no preview)";

    partial void OnAutoCheckChanged(bool value) => OnPropertyChanged(nameof(AutoCheckText));

    partial void OnAutoSaveChanged(bool value) => OnPropertyChanged(nameof(AutoSaveText));
}

/// <summary>A chapter in the preview of a novel about to be added (or of the new chapters about to be put in one).</summary>
public sealed partial class PreviewChapterItem(string title, Scraping.ScrapeOutcome outcome, Books.Chapter? chapter, bool isNew) : ObservableObject
{
    /// <summary>The title in the reading language (the Chinese one until it is converted).</summary>
    [ObservableProperty] private string _displayTitle = title;

    public string Title { get; } = title;

    public Scraping.ScrapeOutcome Outcome { get; } = outcome;

    /// <summary>Its text; null for a chapter that failed or is locked (shown, not saved).</summary>
    public Books.Chapter? Chapter { get; } = chapter;

    public bool IsNew { get; } = isNew;

    public bool IsFailed => Outcome == Scraping.ScrapeOutcome.Failed;

    public bool IsLocked => Outcome == Scraping.ScrapeOutcome.Locked;

    /// <summary>A word after the title: "failed", "locked" or "new".</summary>
    public string Badge => IsFailed ? "failed" : IsLocked ? "locked" : IsNew ? "new" : "";
}

/// <summary>One paragraph of the preview: the converted text, and the Chinese under it when that is shown.</summary>
public sealed record PreviewLine(string Text, string Chinese, bool IsHeading)
{
    public bool HasChinese => Chinese.Length > 0;
}

/// <summary>A chapter in the chapter picker.</summary>
public sealed partial class ChapterItem(int index, string title) : ObservableObject
{
    [ObservableProperty] private string _title = title;

    public int Index { get; } = index;
}

/// <summary>
/// One paragraph on the page (the first one of a chapter is its title). Its converted line is replaced in place when
/// names or options change, so the page keeps its scroll position.
/// </summary>
/// <summary>A word as study mode shows it above its Vietnamese: the Chinese, the pinyin and the Hán Việt reading.</summary>
public sealed record WordReading(string Chinese, string Pinyin, string HanViet)
{
    /// <summary>The readings of every segment of a converted line (null for punctuation and spaces).</summary>
    public static IReadOnlyList<WordReading?> Of(ConvertedLine line, Dictionaries.PinyinDictionary pinyin, Func<string, string> hanViet)
    {
        var readings = new WordReading?[line.Segments.Count];
        for (var i = 0; i < line.Segments.Count; i++)
        {
            var segment = line.Segments[i];
            if (!segment.IsWord || segment.Start < 0 || segment.Start + segment.Length > line.Source.Length) continue;
            var chinese = line.Source.Substring(segment.Start, segment.Length);
            var py = pinyin.Of(chinese);
            var hv = hanViet(chinese);
            // A name reads like one: Lín Wǎn, Lâm Uyển.
            if (segment.Kind == SegmentKind.Name)
            {
                py = Dictionaries.PinyinDictionary.Capitalize(py);
                hv = Dictionaries.PinyinDictionary.Capitalize(hv);
            }
            readings[i] = new WordReading(chinese, py, hv);
        }
        return readings;
    }
}

public sealed partial class ParagraphViewModel(int index, string chinese, bool isHeading) : ObservableObject
{
    private IReadOnlyList<Speech.Sentence>? _sentences;

    [ObservableProperty] private ConvertedLine? _line;
    [ObservableProperty] private bool _isSpeaking;
    [ObservableProperty] private bool _showChinese;

    /// <summary>The sentence being read aloud (-1: none), highlighted inside the paragraph.</summary>
    [ObservableProperty] private int _speakingSentence = -1;

    /// <summary>Where reading stopped last time: marked until the reader scrolls away or starts reading.</summary>
    [ObservableProperty] private bool _isResumePoint;

    /// <summary>Study mode: for each segment of <see cref="Line"/>, its Chinese, pinyin and Hán Việt (null for punctuation); null outside study mode.</summary>
    [ObservableProperty] private IReadOnlyList<WordReading?>? _readings;

    /// <summary>The sentences reading aloud speaks, cut from the converted line.</summary>
    public IReadOnlyList<Speech.Sentence> Sentences => _sentences ??= Speech.SentenceSplitter.Split(Line);

    /// <summary>The highlighted sentence's segments (first, count), or null.</summary>
    public (int First, int Count)? SpeakingSegments =>
        SpeakingSentence >= 0 && SpeakingSentence < Sentences.Count
            ? (Sentences[SpeakingSentence].FirstSegment, Sentences[SpeakingSentence].SegmentCount)
            : null;

    partial void OnSpeakingSentenceChanged(int value) => OnPropertyChanged(nameof(SpeakingSegments));

    public int Index { get; } = index;

    /// <summary>The paragraph as written in the novel.</summary>
    public string Chinese { get; } = chinese;

    public bool IsHeading { get; } = isHeading;

    public string Text => Line?.Text ?? "";

    public bool IsSceneBreak => Line?.IsSceneBreak == true;

    partial void OnLineChanged(ConvertedLine? value)
    {
        _sentences = null;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsSceneBreak));
        OnPropertyChanged(nameof(SpeakingSegments));
    }
}

/// <summary>A saved name or meaning in the names panel.</summary>
public sealed partial class EntryRowViewModel(NovelEntry entry) : ObservableObject
{
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _draft = entry.Vietnamese;

    public NovelEntry Entry { get; } = entry;
    public string Chinese => Entry.Chinese;
    public string Vietnamese => Entry.Vietnamese;

    public bool IsNotEditing => !IsEditing;

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(IsNotEditing));
    public bool ForAllNovels => Entry.BookId is null;

    /// <summary>Added by the name scan (not typed): deleting it keeps the scan from adding it again.</summary>
    public bool IsFound => Entry.Auto;

    public string KindLabel => (Entry.Kind == EntryKind.Name ? "Name" : "Meaning") + (ForAllNovels ? " · all novels" : "") + (Entry.Auto ? " · found automatically" : "");
}

/// <summary>A suggested name: the Chinese, how often it appears and an editable reading.</summary>
public sealed partial class SuggestionRowViewModel(string chinese, int count, string suggested) : ObservableObject
{
    [ObservableProperty] private string _vietnamese = suggested;

    public string Chinese { get; } = chinese;
    public int Count { get; } = count;
    public string CountLabel => Count.ToString(CultureInfo.InvariantCulture) + "×";
}

/// <summary>The cover drawn for a novel without a picture: always the same colours for the same novel.</summary>
public static class CoverArt
{
    private static readonly (string Start, string End)[] Palette =
    [
        ("#4F46E5", "#A78BFA"), ("#0F766E", "#5EEAD4"), ("#B45309", "#FCD34D"), ("#BE123C", "#FDA4AF"),
        ("#1D4ED8", "#93C5FD"), ("#7E22CE", "#F0ABFC"), ("#15803D", "#BEF264"), ("#C2410C", "#FDBA74"),
    ];

    public static (string Start, string End) Colors(string id)
    {
        // A stable hash (string.GetHashCode changes per process).
        var hash = 17;
        foreach (var c in id) hash = unchecked(hash * 31 + c);
        return Palette[(hash & 0x7FFFFFFF) % Palette.Length];
    }

    /// <summary>The first letters of the first two words ("Lâm Uyển truyện" → "LU"), or "?" for an empty title.</summary>
    public static string Initials(string title)
    {
        var words = title.Split([' ', '_', '-', '.', '·'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetter(w[0])).Take(2).Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture));
        var letters = string.Concat(words);
        return letters.Length == 0 ? "?" : letters;
    }
}

/// <summary>Short relative times for the library ("2 h ago").</summary>
public static class NovelFormat
{
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} d ago";
        return when.LocalDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };
}
