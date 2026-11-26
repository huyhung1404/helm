using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Helm.Core.Hotkeys;

namespace Helm.Core.Ui;

/// <summary>
/// A shortcut shown as keycaps in a field-shaped box (40 high, radius 10): what a page shows where the platform has no
/// shortcut recorder (the Gallery, the tests). Pages ask their host for a recorder first (Helm.Ui.Windows' HotkeyBox).
/// </summary>
public static class HotkeyKeys
{
    public static Control Field(HotkeyGesture gesture, string name)
    {
        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        if (gesture.IsEmpty && gesture.Modifiers == HotkeyModifiers.None)
        {
            var none = new TextBlock { Text = "No shortcut" };
            none.Classes.Add("muted");
            keys.Children.Add(none);
        }
        else
        {
            foreach (var cap in gesture.ToKeycaps()) keys.Children.Add(Cap(cap));
        }
        var box = new Border
        {
            MinHeight = 40,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0),
            Child = keys,
        };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("HelmGlassStroke"));
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        ToolTip.SetTip(box, gesture.ToString());
        return box;
    }

    private static Border Cap(string text)
    {
        var cap = new Border
        {
            BorderThickness = new Thickness(1, 1, 1, 2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3),
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold },
        };
        cap.Bind(Border.BackgroundProperty, cap.GetResourceObservable("HelmSubtleFill"));
        cap.Bind(Border.BorderBrushProperty, cap.GetResourceObservable("HelmGlassStroke"));
        return cap;
    }
}
