using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Helm.Core.Desktop;
using Helm.Core.Palette;
using Wpf.Ui.Controls;

namespace Helm.Modules.CommandPalette;

/// <summary>The symbol for a kind of result.</summary>
public sealed class PaletteKindSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PaletteKind.Page => SymbolRegular.Open24,
        PaletteKind.Setting => SymbolRegular.Settings24,
        PaletteKind.Action => SymbolRegular.Flash24,
        PaletteKind.Note => SymbolRegular.Notepad24,
        PaletteKind.Task => SymbolRegular.TaskListSquareLtr24,
        PaletteKind.Debt => SymbolRegular.PersonMoney24,
        PaletteKind.VaultItem => SymbolRegular.Key24,
        PaletteKind.App => SymbolRegular.AppGeneric24,
        PaletteKind.File => SymbolRegular.Document24,
        PaletteKind.Folder => SymbolRegular.Folder24,
        PaletteKind.Window => SymbolRegular.Window24,
        PaletteKind.WindowsSetting => SymbolRegular.WrenchScrewdriver24,
        PaletteKind.Web => SymbolRegular.Globe24,
        _ => SymbolRegular.Search24,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"Note", "App"… shown at the right of a result.</summary>
public sealed class PaletteKindNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PaletteKind.Page => "Page",
        PaletteKind.Setting => "Setting",
        PaletteKind.Action => "Action",
        PaletteKind.Note => "Note",
        PaletteKind.Task => "Task",
        PaletteKind.Debt => "Debt",
        PaletteKind.VaultItem => "Vault",
        PaletteKind.App => "App",
        PaletteKind.File => "File",
        PaletteKind.Folder => "Folder",
        PaletteKind.Window => "Window",
        PaletteKind.WindowsSetting => "Windows",
        PaletteKind.Web => "Web",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A file path → its Explorer icon (null leaves the symbol showing).</summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? ShellIcons.ForFile(path) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value is null (the symbol shows only when there is no file icon).</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
