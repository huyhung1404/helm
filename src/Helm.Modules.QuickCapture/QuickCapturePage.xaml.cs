using System.Windows.Controls;
using Helm.Core.Hotkeys;
using Helm.Modules.QuickCapture.Ui;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Quick Capture's page (Home → Utilities): the shared Avalonia <see cref="QuickCaptureView"/> inside this WPF page. What
/// only Windows has comes from here: the shortcut recorder.
/// </summary>
public partial class QuickCapturePage : Page, IQuickCapturePageHost
{
    public QuickCapturePage(QuickCaptureViewModel viewModel)
    {
        InitializeComponent();
        Host.Show(() => new QuickCaptureView(viewModel, this));
    }

    public Avalonia.Controls.Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) =>
        Helm.Ui.Windows.HotkeyBox.Create(gesture, changed);
}
