using System.Windows.Controls;
using Helm.Core.Hotkeys;
using Helm.Modules.Keyboard.Ui;

namespace Helm.Modules.Keyboard;

/// <summary>
/// Helm Keyboard's page (Home → Utilities): the shared Avalonia <see cref="KeyboardView"/> inside this WPF page. What only
/// Windows has comes from here: the shortcut recorder.
/// </summary>
public partial class KeyboardPage : Page, IKeyboardPageHost
{
    public KeyboardPage(KeyboardViewModel viewModel)
    {
        InitializeComponent();
        Host.Show(() => new KeyboardView(viewModel, this));
    }

    public Avalonia.Controls.Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) =>
        Helm.Ui.Windows.HotkeyBox.Create(gesture, changed);
}
