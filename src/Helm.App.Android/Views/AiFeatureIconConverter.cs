using System.Globalization;
using Avalonia.Data.Converters;
using Helm.Ai;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.App.Android.Views;

/// <summary>
/// A feature's icon in General › AI features: its tool's icon (as in the drawer) while that tool is in the app, else its
/// Fluent symbol. <c>Content="{Binding Converter={StaticResource AiFeatureIcon}}"</c> on an <see cref="AiFeatureRow"/>.
/// </summary>
public sealed class AiFeatureIconConverter : IValueConverter
{
    private const double Size = 22;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AiFeatureRow row) return null;
        if (row.Tool is { } tool && App.Services.GetService<IModuleHost<IAndroidModule>>()?.Find(tool) is { } module)
            return new ModuleIcon { Symbol = module.Icon, Image = module.IconImage, Size = Size };
        return new ModuleIcon { Symbol = Enum.TryParse<Symbol>(row.Symbol, out var symbol) ? symbol : Symbol.Sparkle, Size = Size };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
