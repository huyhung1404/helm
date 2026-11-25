using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FluentIcons.Avalonia;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Core.Ui;

/// <summary>
/// A tool's icon, like ModuleIcon on Windows: its picture (<see cref="Helm.Core.Modules.IAndroidModule.IconImage"/>)
/// when it has one, else its Fluent symbol drawn in this control's Foreground.
/// <code>&lt;ui:ModuleIcon Symbol="{Binding Icon}" Image="{Binding IconImage}" Size="30" /&gt;</code>
/// </summary>
public sealed class ModuleIcon : UserControl
{
    public static readonly StyledProperty<Symbol> SymbolProperty =
        AvaloniaProperty.Register<ModuleIcon, Symbol>(nameof(Symbol), Symbol.Apps);

    public static readonly StyledProperty<IImage?> ImageProperty =
        AvaloniaProperty.Register<ModuleIcon, IImage?>(nameof(Image));

    /// <summary>Width and height of a picture, and the font size of a symbol, in DIPs.</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ModuleIcon, double>(nameof(Size), 20);

    public ModuleIcon()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Rebuild();
    }

    public Symbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public IImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SymbolProperty || change.Property == ImageProperty || change.Property == SizeProperty) Rebuild();
    }

    private void Rebuild()
    {
        if (Image is { } image)
        {
            var picture = new Image { Source = image, Width = Size, Height = Size };
            // Pictures are drawn far smaller than their source (256 px into 20-64 px); keep the thin strokes.
            RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.HighQuality);
            Content = picture;
        }
        else
        {
            // No Foreground here: the symbol inherits this control's, so hosts can colour it.
            Content = new SymbolIcon { Symbol = Symbol, FontSize = Size };
        }
    }
}
