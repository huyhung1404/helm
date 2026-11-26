using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Helm.Core.Hotkeys;

namespace Helm.Ui.Windows;

/// <summary>
/// Records a global shortcut inside an Avalonia page (the Windows-only part of a shared view, for example Vault's
/// auto-type): shows the current one as text; a click waits for the next key combination, Esc cancels. Global hotkeys
/// are suspended while it waits (<see cref="HotkeyRecording"/>), so Helm's own shortcuts do not fire meanwhile.
/// </summary>
public sealed class HotkeyBox : Button
{
    public static readonly StyledProperty<HotkeyGesture> GestureProperty =
        AvaloniaProperty.Register<HotkeyBox, HotkeyGesture>(nameof(Gesture), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Looks like any Button: the theme's Button styles (ghost, the template) match by exact type.</summary>
    protected override Type StyleKeyOverride => typeof(Button);

    private IDisposable? _recording;
    private string _hint = "";

    public HotkeyBox()
    {
        Classes.Add("ghost");
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetName(this, "Shortcut");
        LostFocus += (_, _) => End();
        Refresh();
    }

    /// <summary>A recorder for a shared page's host (IXxxPageHost.CreateHotkeyPicker): starts at <paramref name="gesture"/>, tells each new one.</summary>
    public static HotkeyBox Create(HotkeyGesture gesture, Action<HotkeyGesture> changed)
    {
        var box = new HotkeyBox { Gesture = gesture, MinHeight = 40 };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == GestureProperty) changed(box.Gesture);
        };
        return box;
    }

    public HotkeyGesture Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GestureProperty) Refresh();
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (_recording is not null) return;
        _recording = HotkeyRecording.Begin();
        _hint = "Press the new shortcut… (Esc cancels)";
        Refresh();
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        End();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_recording is null)
        {
            base.OnKeyDown(e);
            return;
        }
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            End();
            return;
        }
        var modifiers = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyModifiers.Win;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyModifiers.Shift;
        var virtualKey = Avalonia.Win32.Input.KeyInterop.VirtualKeyFromKey(e.Key);
        if (virtualKey == 0 || VirtualKeyNames.IsModifier(virtualKey))
        {
            // Only a modifier so far: show it, keep waiting.
            _hint = new HotkeyGesture(modifiers, 0).ToKeycaps().Count > 0 ? string.Join(" + ", new HotkeyGesture(modifiers, 0).ToKeycaps()) + " + …" : "Press the new shortcut… (Esc cancels)";
            Refresh();
            return;
        }
        var isFunctionKey = virtualKey is >= VirtualKeyNames.F1 and <= VirtualKeyNames.F24;
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            _hint = "Add Win, Ctrl, Alt or Shift (Esc cancels)";
            Refresh();
            return;
        }
        if (modifiers == HotkeyModifiers.Shift && !isFunctionKey)
        {
            _hint = "Shift alone would block typing: add Win, Ctrl or Alt";
            Refresh();
            return;
        }
        Gesture = new HotkeyGesture(modifiers, virtualKey);
        End();
    }

    private void End()
    {
        if (_recording is null) return;
        _recording.Dispose();
        _recording = null;
        Refresh();
    }

    private void Refresh() => Content = _recording is not null ? _hint : Gesture.IsEmpty ? "Off — click to set a shortcut" : Gesture.ToString();
}
