using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Helm.Modules.Wallet;

/// <summary>A category's icon on a soft disc of its colour; the same badge in lists, chips, pickers and the legend.</summary>
public sealed class CategoryBadge : Border
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(CategoryBadge), new PropertyMetadata(WalletIcons.Fallback, (d, _) => ((CategoryBadge)d).Update()));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(int), typeof(CategoryBadge), new PropertyMetadata(0, (d, _) => ((CategoryBadge)d).Update()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(CategoryBadge), new PropertyMetadata(32.0, (d, _) => ((CategoryBadge)d).Update()));

    private readonly SymbolIcon _symbol = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public CategoryBadge()
    {
        Child = _symbol;
        Focusable = false;
        // Theme brushes are read when drawn: refresh once the badge is in the window.
        Loaded += (_, _) => Update();
        Update();
    }

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The colour slot (<see cref="WalletPalette"/>); -1 is "Other".</summary>
    public int Color
    {
        get => (int)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void Update()
    {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size / 2);
        Background = WalletTheme.Brush(Color, 0x30, this);
        _symbol.Symbol = (SymbolRegular)WalletConverters.Symbol.Convert(Icon, typeof(SymbolRegular), null, System.Globalization.CultureInfo.InvariantCulture);
        _symbol.FontSize = Math.Round(Size * 0.55);
        _symbol.Foreground = WalletTheme.Brush(Color, 0xFF, this);
    }
}
