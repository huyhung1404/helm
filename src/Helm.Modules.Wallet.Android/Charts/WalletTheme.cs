using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using FluentIcons.Common;

namespace Helm.Modules.Wallet;

/// <summary>The theme the Wallet charts and badges draw in, and their brushes.</summary>
internal static class WalletTheme
{
    public static bool IsDark(StyledElement? element) =>
        (element?.ActualThemeVariant ?? Application.Current?.ActualThemeVariant) == ThemeVariant.Dark;

    public static IBrush Brush(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));

    /// <summary>A category colour (-1 is "Other") with an alpha.</summary>
    public static IBrush Category(int slot, byte alpha, bool dark) =>
        Brush(WalletPalette.WithAlpha(slot < 0 ? WalletPalette.Other(dark) : WalletPalette.Color(slot, dark), alpha));

    public static IBrush Primary(Control control) => TextElement.GetForeground(control) ?? Brushes.Gray;

    public static IBrush Secondary(Control control) =>
        control.TryFindResource("HelmSecondaryText", control.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    public static Typeface Typeface(Control control, FontWeight weight = FontWeight.Normal) => new(TextElement.GetFontFamily(control), FontStyle.Normal, weight);

    public static FormattedText Text(Control control, string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface(control, weight), size, brush);
}

/// <summary>Category icon names and colour slots to Fluent symbols and brushes, for the pages' XAML.</summary>
public static class WalletConverters
{
    /// <summary>"12,480,000 ₫" out of "Balance after: 12,480,000 ₫" (the row is labelled already).</summary>
    public static IValueConverter BalanceOnly { get; } = new FuncValueConverter<string?, string>(v => v?.Replace("Balance after: ", "") ?? "");

    /// <summary>A dash for a field left empty.</summary>
    public static IValueConverter OrDash { get; } = new FuncValueConverter<string?, string>(v => string.IsNullOrWhiteSpace(v) ? "—" : v);

    /// <summary>A person's avatar disc: one of the category colours, picked by the name, faint.</summary>
    public static IValueConverter PersonFill { get; } = new FuncValueConverter<string?, IBrush>(n => WalletTheme.Category(Slot(n), 0x30, WalletTheme.IsDark(null)));

    /// <summary>A person's initial, in the solid colour of their disc.</summary>
    public static IValueConverter PersonInk { get; } = new FuncValueConverter<string?, IBrush>(n => WalletTheme.Category(Slot(n), 0xFF, WalletTheme.IsDark(null)));

    // The same name always gets the same colour (a stable hash, not string.GetHashCode).
    private static int Slot(string? name) => (name ?? "").ToLowerInvariant().Aggregate(7, (h, c) => (h * 31 + c) & 0x7FFFFFFF) % WalletPalette.Slots + 1;

    /// <summary>A kind of transaction as the phone's switch says it: "Between my accounts" is "Transfer".</summary>
    public static IValueConverter KindName { get; } = new FuncValueConverter<string?, string>(v => v == "Between my accounts" ? "Transfer" : v ?? "");

    /// <summary>An icon name (<see cref="WalletIcons"/>) to its Fluent symbol.</summary>
    public static IValueConverter Symbol { get; } = new FuncValueConverter<string?, Symbol>(v =>
        Enum.TryParse<Symbol>(WalletIcons.Normalize(v), out var s) ? s : FluentIcons.Common.Symbol.Tag);

    /// <summary>A colour slot to the solid category colour (a swatch, a legend bar).</summary>
    public static IValueConverter Ink { get; } = new FuncValueConverter<int, IBrush>(v => WalletTheme.Category(v, 0xFF, WalletTheme.IsDark(null)));
}
