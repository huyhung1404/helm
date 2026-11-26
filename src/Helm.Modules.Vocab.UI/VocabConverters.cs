using Avalonia.Data.Converters;

namespace Helm.Modules.Vocab.Ui;

/// <summary>The converters Vocabulary's views bind with.</summary>
public static class VocabConverters
{
    /// <summary>A word's tree as the rows of the phone's outline.</summary>
    public static readonly IValueConverter Outline = new FuncValueConverter<VocabNodeViewModel?, IReadOnlyList<VocabOutlineRow>>(VocabOutlineRow.Flatten);

    /// <summary>"3 of 21" (the view models say "3 / 21").</summary>
    public static readonly IValueConverter Position = new FuncValueConverter<string, string>(p => p?.Replace(" / ", " of ") ?? "");

    /// <summary>A filter chip's label: the list's "All words" is just "All" among the chips.</summary>
    public static readonly IValueConverter ChipName = new FuncValueConverter<string, string>(n => n == "All words" ? "All" : n ?? "");

    /// <summary>A small label in capitals ("MEANING", "SOUND").</summary>
    public static readonly IValueConverter Upper = new FuncValueConverter<string, string>(t => t?.ToUpperInvariant() ?? "");

    /// <summary>"#Aptis": one of the user's tags.</summary>
    public static readonly IValueConverter Hashtag = new FuncValueConverter<string, string>(t => "#" + t);

    /// <summary>A count above zero.</summary>
    public static readonly IValueConverter Any = new FuncValueConverter<int, bool>(n => n > 0);

    /// <summary>The Learn card's typing box: how pinyin may be typed, or what to type.</summary>
    public static readonly IValueConverter TypePlaceholder = new FuncValueConverter<bool, string>(pinyin => pinyin ? "bujiu, bu3jiu4… — press Enter" : "Type the word — press Enter");
}
