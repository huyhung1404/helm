using System.Windows.Controls;
using Helm.Core.Hotkeys;
using Helm.Modules.AlwaysOnTop.Ui;

namespace Helm.Modules.AlwaysOnTop;

/// <summary>
/// Always On Top's page (Home → Utilities): the shared Avalonia <see cref="AlwaysOnTopView"/> inside this WPF page. What
/// only Windows has comes from here: the shortcut recorder.
/// </summary>
public partial class AlwaysOnTopPage : Page, IAlwaysOnTopPageHost
{
    public AlwaysOnTopPage(AlwaysOnTopViewModel viewModel)
    {
        InitializeComponent();
        Host.Show(() => new AlwaysOnTopView(viewModel, this));
    }

    public Avalonia.Controls.Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed)
    {
        var box = new Helm.Ui.Windows.HotkeyBox { Gesture = gesture, MinHeight = 40 };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == Helm.Ui.Windows.HotkeyBox.GestureProperty) changed(box.Gesture);
        };
        return box;
    }
}
