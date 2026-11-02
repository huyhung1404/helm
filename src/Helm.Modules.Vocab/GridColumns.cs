using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>pc:GridColumns.Widths="1.15*,16,1.6*,16,56"</c> on a Grid (or in a Style): its columns from one list, so the
/// Words list's column titles and every row share them without repeating the definitions.
/// </summary>
public static class GridColumns
{
    public static readonly DependencyProperty WidthsProperty =
        DependencyProperty.RegisterAttached("Widths", typeof(string), typeof(GridColumns), new PropertyMetadata("", OnChanged));

    public static string GetWidths(DependencyObject o) => (string)o.GetValue(WidthsProperty);

    public static void SetWidths(DependencyObject o, string value) => o.SetValue(WidthsProperty, value);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Grid grid) return;
        var converter = new GridLengthConverter();
        grid.ColumnDefinitions.Clear();
        foreach (var width in ((e.NewValue as string) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)converter.ConvertFromInvariantString(width)! });
    }
}
