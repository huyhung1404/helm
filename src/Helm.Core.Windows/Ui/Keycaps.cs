using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Helm.Core.Hotkeys;

namespace Helm.Core.Ui;

/// <summary>Renders a key combination as keycaps (Win logo, Ctrl, T …), like the PowerToys shortcut list.</summary>
public sealed class Keycaps : WrapPanel
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(HotkeyGesture), typeof(Keycaps), new PropertyMetadata(default(HotkeyGesture), (d, _) => ((Keycaps)d).Rebuild()));

    public static readonly DependencyProperty KeysProperty = DependencyProperty.Register(
        nameof(Keys), typeof(IEnumerable), typeof(Keycaps), new PropertyMetadata(null, (d, _) => ((Keycaps)d).Rebuild()));

    public static readonly DependencyProperty IsAccentProperty = DependencyProperty.Register(
        nameof(IsAccent), typeof(bool), typeof(Keycaps), new PropertyMetadata(false, (d, _) => ((Keycaps)d).Rebuild()));

    public Keycaps()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public HotkeyGesture Gesture
    {
        get => (HotkeyGesture)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <summary>Alternative to <see cref="Gesture"/>: arbitrary labels such as "Shift" or "Drag".</summary>
    public IEnumerable? Keys
    {
        get => (IEnumerable?)GetValue(KeysProperty);
        set => SetValue(KeysProperty, value);
    }

    /// <summary>Filled accent caps (settings pages) instead of subtle caps (Home).</summary>
    public bool IsAccent
    {
        get => (bool)GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        IEnumerable<string> labels = Keys?.Cast<object>().Select(o => o?.ToString() ?? string.Empty) ?? Gesture.ToKeycaps();
        foreach (var label in labels) Children.Add(CreateCap(label));
    }

    private Border CreateCap(string label)
    {
        var cap = new Border
        {
            MinWidth = IsAccent ? 34 : 26,
            Height = IsAccent ? 32 : 26,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(7, 0, 7, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };

        if (IsAccent)
        {
            cap.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            cap.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        }
        else
        {
            cap.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            cap.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        }

        var foregroundKey = IsAccent ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush";
        if (label == "Win")
        {
            cap.Child = CreateWindowsLogo(foregroundKey, IsAccent ? 14 : 12);
        }
        else
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
            cap.Child = text;
        }
        return cap;
    }

    private static UIElement CreateWindowsLogo(string brushKey, double size)
    {
        var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var half = (size - 1) / 2;
        foreach (var (row, col) in new[] { (0, 0), (0, 1), (1, 0), (1, 1) })
        {
            var square = new Rectangle
            {
                Width = half,
                Height = half,
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = row == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            };
            square.SetResourceReference(Shape.FillProperty, brushKey);
            grid.Children.Add(square);
        }
        RenderOptions.SetEdgeMode(grid, EdgeMode.Aliased);
        return grid;
    }
}
