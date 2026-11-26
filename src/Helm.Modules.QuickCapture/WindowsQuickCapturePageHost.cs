using Avalonia.Controls;
using Helm.Core.Hotkeys;
using Helm.Modules.QuickCapture.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.QuickCapture;

/// <summary>What the shared <see cref="QuickCaptureView"/> needs from Windows: the shortcut recorder.</summary>
internal sealed class WindowsQuickCapturePageHost : IQuickCapturePageHost
{
    public Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) => HotkeyBox.Create(gesture, changed);
}
