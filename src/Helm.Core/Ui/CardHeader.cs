using System.Windows;
using System.Windows.Controls;

namespace Helm.Core.Ui;

/// <summary>Title + secondary description, used as the Header of every settings CardControl/CardExpander.</summary>
public sealed class CardHeader : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(CardHeader), new PropertyMetadata(string.Empty, (d, e) => ((CardHeader)d)._title.Text = (string)e.NewValue));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(CardHeader), new PropertyMetadata(null, OnDescriptionChanged));

    private readonly TextBlock _title = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public CardHeader()
    {
        VerticalAlignment = VerticalAlignment.Center;
        _description.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Children.Add(_title);
        Children.Add(_description);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (CardHeader)d;
        var text = e.NewValue as string;
        self._description.Text = text ?? string.Empty;
        self._description.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
