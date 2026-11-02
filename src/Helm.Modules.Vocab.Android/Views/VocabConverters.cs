using Avalonia.Data.Converters;

namespace Helm.Modules.Vocab;

/// <summary>The converters Vocabulary's pages bind with.</summary>
public static class VocabConverters
{
    /// <summary>A word's tree as the rows of its outline.</summary>
    public static readonly IValueConverter Outline = new FuncValueConverter<VocabNodeViewModel?, IReadOnlyList<VocabOutlineRow>>(VocabOutlineRow.Flatten);

    /// <summary>The Words list's order by its index ("A–Z", "Newest first"…), for the Order button.</summary>
    public static readonly IValueConverter SortName = new FuncValueConverter<int, string>(i =>
        VocabWordListViewModel.SortNames[Math.Clamp(i, 0, VocabWordListViewModel.SortNames.Count - 1)]);

    /// <summary>A bottom sheet's tallest: nine tenths of the page it covers.</summary>
    public static readonly IValueConverter SheetHeight = new FuncValueConverter<double, double>(h => h > 0 ? h * 0.9 : double.PositiveInfinity);

    /// <summary>The edit sheet's height: nearly the whole page.</summary>
    public static readonly IValueConverter FullSheetHeight = new FuncValueConverter<double, double>(h => h > 0 ? Math.Round(h * 0.94) : double.NaN);

    /// <summary>"#Aptis": one of the user's tags.</summary>
    public static readonly IValueConverter Hashtag = new FuncValueConverter<string, string>(t => "#" + t);
}
