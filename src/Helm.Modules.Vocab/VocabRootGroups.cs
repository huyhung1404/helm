using System.Globalization;
using System.Windows.Data;

namespace Helm.Modules.Vocab;

/// <summary>The Roots tab's groups of family cards: parts shared by several words, then parts in one word so far.</summary>
public sealed class VocabRootGroups : IValueConverter
{
    public const string Families = "Families";
    public const string Single = "In one word so far";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VocabRootRow { Words.Count: > 1 } ? Families : Single;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
