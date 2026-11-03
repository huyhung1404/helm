using Avalonia.Data.Converters;

namespace Helm.Modules.Vocab;

/// <summary>The converters Vocabulary's pages bind with.</summary>
public static class VocabConverters
{
    /// <summary>The design's line-height: 1.4 times the text's size (VocabStyles' <c>.css TextBlock</c>).</summary>
    public static readonly IValueConverter Leading = new FuncValueConverter<double, double>(size => Math.Round(size * 1.4, 1));

    /// <summary>A word's tree as the rows of its outline.</summary>
    public static readonly IValueConverter Outline = new FuncValueConverter<VocabNodeViewModel?, IReadOnlyList<VocabOutlineRow>>(VocabOutlineRow.Flatten);

    /// <summary>The Words list's order by its index ("A–Z", "Newest first"…), for the Order button.</summary>
    public static readonly IValueConverter SortName = new FuncValueConverter<int, string>(i =>
        VocabWordListViewModel.SortNames[Math.Clamp(i, 0, VocabWordListViewModel.SortNames.Count - 1)]);

    /// <summary>A bottom sheet's tallest (.sheet's max-height: 92%) of the page it covers.</summary>
    public static readonly IValueConverter SheetHeight = new FuncValueConverter<double, double>(h => h > 0 ? h * 0.92 : double.PositiveInfinity);

    /// <summary>The edit sheet's height (.sheet.full: 92%).</summary>
    public static readonly IValueConverter FullSheetHeight = new FuncValueConverter<double, double>(h => h > 0 ? Math.Round(h * 0.92) : double.NaN);

    /// <summary>The Learn card's typing box: an example of how pinyin may be typed, or what to type.</summary>
    public static readonly IValueConverter TypePlaceholder = new FuncValueConverter<bool, string>(pinyin => pinyin ? "bujiu, bu3jiu4…" : "Type the word");

    /// <summary>"3 of 21" for the app bar ("3 / 21" in the shared view models).</summary>
    public static readonly IValueConverter Position = new FuncValueConverter<string, string>(p => p?.Replace(" / ", " of ") ?? "");

    /// <summary>A filter chip's label: the list's "All words" is just "All" among the chips.</summary>
    public static readonly IValueConverter ChipName = new FuncValueConverter<string, string>(n => n == "All words" ? "All" : n ?? "");

    /// <summary>A small label in capitals ("MEANING", "SOUND").</summary>
    public static readonly IValueConverter Upper = new FuncValueConverter<string, string>(t => t?.ToUpperInvariant() ?? "");

    /// <summary>
    /// The answer bar's wording, shorter on the phone so the two buttons keep it on one line: "Knew meaning",
    /// "again in 10 min", "pinyin slip · tomorrow".
    /// </summary>
    public static readonly IValueConverter Short = new FuncValueConverter<string, string>(t => (t ?? "")
        .Replace("Forgot the meaning", "Forgot meaning").Replace("Knew the meaning", "Knew meaning")
        .Replace("pinyin slip · back ", "pinyin slip · ").Replace(" minutes", " min").Replace(" minute", " min")
        .Replace("Press “Knew meaning”: a pinyin slip never counts", "Tap “Knew meaning”: it never counts"));

    /// <summary>A word easy to mix up with ("relevant: liên quan…" from <see cref="VocabWordInfo.Confusables"/>): the word.</summary>
    public static readonly IValueConverter MixWord = new FuncValueConverter<string, string>(t => t is null ? "" : t.IndexOf(": ", StringComparison.Ordinal) is var at and >= 0 ? t[..at] : t);

    /// <summary>The rest after the word, ": liên quan…", or nothing.</summary>
    public static readonly IValueConverter MixNote = new FuncValueConverter<string, string>(t => t is null ? "" : t.IndexOf(": ", StringComparison.Ordinal) is var at and >= 0 ? t[at..] : "");

    /// <summary>"#Aptis": one of the user's tags.</summary>
    public static readonly IValueConverter Hashtag = new FuncValueConverter<string, string>(t => "#" + t);
}
