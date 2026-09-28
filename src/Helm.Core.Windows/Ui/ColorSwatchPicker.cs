using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Helm.Core.Geometry;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Helm.Core.Ui;

/// <summary>A swatch button that opens a palette of preset colors plus a hex text box. Binds "#RRGGBB" strings.</summary>
public sealed class ColorSwatchPicker : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorSwatchPicker),
        new FrameworkPropertyMetadata("#0078D4", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorSwatchPicker)d).Refresh()));

    private static readonly string[] s_presets =
    [
        "#0078D4", "#0099BC", "#00B7C3", "#00CC6A", "#10893E", "#107C10", "#7E735F", "#FFB900",
        "#F7630C", "#E81123", "#EA005E", "#C239B3", "#8764B8", "#744DA9", "#FFFFFF", "#767676",
    ];

    private readonly Border _swatch = new() { Width = 40, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly TextBox _hex = new() { Width = 120, Margin = new Thickness(0, 12, 0, 0), PlaceholderText = "#RRGGBB" };
    private readonly Popup _popup;

    public ColorSwatchPicker()
    {
        _swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var open = new Wpf.Ui.Controls.Button
        {
            Content = _swatch,
            Padding = new Thickness(6),
            Icon = new SymbolIcon(SymbolRegular.ChevronDown16),
        };
        open.Click += (_, _) => _popup!.IsOpen = true;

        var grid = new UniformGrid { Columns = 8 };
        foreach (var preset in s_presets)
        {
            var b = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(2),
                Background = BrushFrom(preset),
                BorderThickness = new Thickness(1),
                ToolTip = preset,
                Tag = preset,
                Template = SwatchTemplate(),
            };
            b.SetResourceReference(Button.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            b.Click += (s, _) =>
            {
                Color = (string)((Button)s).Tag;
                _popup!.IsOpen = false;
            };
            grid.Children.Add(b);
        }

        _hex.LostFocus += (_, _) => ApplyHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { ApplyHex(); _popup!.IsOpen = false; }
        };

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(_hex);
        var border = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");

        _popup = new Popup { Child = border, PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        Content = open;
        Refresh();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private void ApplyHex()
    {
        if (RgbColor.TryParse(_hex.Text, out var c)) Color = c.ToHex();
        else _hex.Text = Color;
    }

    private void Refresh()
    {
        _swatch.Background = BrushFrom(Color);
        _hex.Text = Color;
    }

    private static SolidColorBrush BrushFrom(string? hex)
    {
        var c = RgbColor.Parse(hex, new RgbColor(0, 120, 212));
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
