using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Shapes;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Helm.App.Views;

/// <summary>
/// A tool group's title in the navigation pane: a short line in the collapsed icon strip, the group's name when the
/// pane is open. Both are the same height, so opening the pane never moves an item under the mouse.
/// </summary>
internal sealed class NavGroupHeader : Grid
{
    public NavGroupHeader(string title, NavigationView nav)
    {
        Height = 28;
        var line = new Rectangle
        {
            Width = 20,
            Height = 1,
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.SetResourceReference(Shape.FillProperty, "DividerStrokeColorDefaultBrush");
        line.SetBinding(VisibilityProperty, new Binding(nameof(NavigationView.IsPaneOpen))
        {
            Source = nav,
            Converter = PaneClosedToVisible.Instance,
        });

        var caption = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        caption.SetBinding(VisibilityProperty, new Binding(nameof(NavigationView.IsPaneOpen))
        {
            Source = nav,
            Converter = new BooleanToVisibilityConverter(),
        });

        Children.Add(line);
        Children.Add(caption);
        System.Windows.Automation.AutomationProperties.SetName(this, title);
    }

    private sealed class PaneClosedToVisible : IValueConverter
    {
        public static readonly PaneClosedToVisible Instance = new();

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
