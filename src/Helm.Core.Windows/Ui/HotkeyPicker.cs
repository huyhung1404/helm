using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Helm.Core.Hotkeys;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Helm.Core.Ui;

/// <summary>
/// Shows the current shortcut as accent keycaps with a pencil; clicking opens a popup that records the next
/// key combination. Global hotkeys are suspended while recording (see <see cref="HotkeyRecording"/>).
/// </summary>
public sealed class HotkeyPicker : UserControl
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(HotkeyGesture), typeof(HotkeyPicker),
        new FrameworkPropertyMetadata(default(HotkeyGesture), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyPicker)d)._display.Gesture = (HotkeyGesture)e.NewValue));

    public static readonly DependencyProperty DefaultGestureProperty = DependencyProperty.Register(
        nameof(DefaultGesture), typeof(HotkeyGesture), typeof(HotkeyPicker), new PropertyMetadata(default(HotkeyGesture)));

    private readonly Keycaps _display = new() { IsAccent = true };
    private readonly Keycaps _preview = new() { IsAccent = true, MinHeight = 32 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
    private readonly Popup _popup;
    private readonly Button _save;
    private HotkeyGesture _pending;
    private IDisposable? _recording;

    public HotkeyPicker()
    {
        var edit = new Button
        {
            Icon = new SymbolIcon(SymbolRegular.Edit20),
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(6),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Edit shortcut",
        };
        edit.Click += (_, _) => Open();

        var root = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(_display);
        root.Children.Add(edit);

        _save = new Button { Content = "Save", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        _save.Click += (_, _) => Commit(_pending);
        var reset = new Button { Content = "Reset", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => Commit(DefaultGesture);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_save);
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);

        var title = new TextBlock { Text = "Activation shortcut", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var subtitle = new TextBlock { Text = "Press a combination of keys to change this shortcut.", FontSize = 12, Margin = new Thickness(0, 0, 0, 16) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var panel = new StackPanel { Width = 340 };
        panel.Children.Add(title);
        panel.Children.Add(subtitle);
        panel.Children.Add(_preview);
        panel.Children.Add(_hint);
        panel.Children.Add(buttons);

        var border = new Border
        {
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = panel,
            Focusable = true,
        };
        border.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        border.PreviewKeyDown += OnPopupKeyDown;

        _popup = new Popup
        {
            Child = border,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        _popup.Opened += (_, _) => border.Focus();
        _popup.Closed += (_, _) => EndRecording();

        Content = root;
    }

    public HotkeyGesture Gesture
    {
        get => (HotkeyGesture)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public HotkeyGesture DefaultGesture
    {
        get => (HotkeyGesture)GetValue(DefaultGestureProperty);
        set => SetValue(DefaultGestureProperty, value);
    }

    private void Open()
    {
        _pending = Gesture;
        _preview.Gesture = _pending;
        _save.IsEnabled = false;
        _hint.Text = "Use Win, Ctrl, Alt or Shift plus a key. Esc cancels.";
        _recording ??= HotkeyRecording.Begin();
        _popup.IsOpen = true;
    }

    private void Close() => _popup.IsOpen = false;

    private void Commit(HotkeyGesture gesture)
    {
        Gesture = gesture;
        Close();
    }

    private void EndRecording()
    {
        _recording?.Dispose();
        _recording = null;
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Close();
            return;
        }

        var modifiers = CurrentModifiers();
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (VirtualKeyNames.IsModifier(vk))
        {
            _preview.Gesture = new HotkeyGesture(modifiers, 0);
            return;
        }

        var candidate = new HotkeyGesture(modifiers, vk);
        _preview.Gesture = candidate;
        var isFunctionKey = vk is >= VirtualKeyNames.F1 and <= VirtualKeyNames.F24;
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            _hint.Text = "A shortcut needs at least one modifier (Win, Ctrl, Alt or Shift).";
            _save.IsEnabled = false;
            return;
        }
        if (modifiers == HotkeyModifiers.Shift && !isFunctionKey)
        {
            _hint.Text = "Shift alone would block typing; add Win, Ctrl or Alt.";
            _save.IsEnabled = false;
            return;
        }

        _pending = candidate;
        _hint.Text = "Press Save to keep this shortcut.";
        _save.IsEnabled = true;
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) m |= HotkeyModifiers.Win;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) m |= HotkeyModifiers.Ctrl;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) m |= HotkeyModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) m |= HotkeyModifiers.Shift;
        return m;
    }
}
