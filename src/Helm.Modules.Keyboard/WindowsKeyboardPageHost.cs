using Avalonia.Controls;
using Helm.Core.Hotkeys;
using Helm.Modules.Keyboard.Ui;

namespace Helm.Modules.Keyboard;

/// <summary>
/// Helm Keyboard's page (Home → Utilities): the shared <see cref="KeyboardView"/>. What only Windows has comes from
/// here: the shortcut recorder.
/// </summary>
internal sealed class WindowsKeyboardPageHost : IKeyboardPageHost
{
    public WindowsKeyboardPageHost(KeyboardViewModel viewModel) => View = new KeyboardView(viewModel, this);

    /// <summary>The page the shell shows.</summary>
    public Control View { get; }

    public Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) => Helm.Ui.Windows.HotkeyBox.Create(gesture, changed);
}
