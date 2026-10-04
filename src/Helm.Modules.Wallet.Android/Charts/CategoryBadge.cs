using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentIcons.Avalonia;

namespace Helm.Modules.Wallet;

/// <summary>A category's icon on a soft disc of its colour; the same badge in lists, chips, pickers and the legend.</summary>
public sealed class CategoryBadge : Border
{
    public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<CategoryBadge, string>(nameof(Icon), WalletIcons.Fallback);

    public static readonly StyledProperty<int> ColorProperty = AvaloniaProperty.Register<CategoryBadge, int>(nameof(Color));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<CategoryBadge, double>(nameof(Size), 32);

    private readonly SymbolIcon _symbol = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public CategoryBadge()
    {
        Child = _symbol;
        ActualThemeVariantChanged += (_, _) => Update();
        Update();
    }

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The colour slot (<see cref="WalletPalette"/>); -1 is "Other".</summary>
    public int Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == ColorProperty || change.Property == SizeProperty) Update();
    }

    private void Update()
    {
        var dark = WalletTheme.IsDark(this);
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size / 2);
        Background = WalletTheme.Category(Color, 0x30, dark);
        _symbol.Symbol = (FluentIcons.Common.Symbol)WalletConverters.Symbol.Convert(Icon, typeof(FluentIcons.Common.Symbol), null, System.Globalization.CultureInfo.InvariantCulture)!;
        _symbol.FontSize = Math.Round(Size * 0.55);
        _symbol.Foreground = WalletTheme.Category(Color, 0xFF, dark);
    }
}
