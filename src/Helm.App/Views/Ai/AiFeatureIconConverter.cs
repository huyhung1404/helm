using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Helm.Ai;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Helm.App.Views.Ai;

/// <summary>
/// A feature's icon in the Features table: its tool's icon (the same picture as in the menu) while that tool is in the
/// app, else its Fluent symbol. <c>Content="{Binding Converter={StaticResource AiFeatureIcon}}"</c> on an
/// <see cref="AiFeatureRow"/>.
/// </summary>
internal sealed class AiFeatureIconConverter : IValueConverter
{
    private const double Size = 18;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AiFeatureRow row) return null;
        if (row.Tool is { } tool && (Application.Current as App)?.Services.GetService<IModuleHost>()?.Find(tool) is { } module)
            return ModuleIcon.Create(module, Size, Size);
        return new SymbolIcon
        {
            Symbol = Enum.TryParse<SymbolRegular>(row.Symbol + "24", out var symbol) ? symbol : SymbolRegular.Sparkle24,
            FontSize = Size,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
