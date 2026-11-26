using System.Windows.Controls;
using Avalonia.Media;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Modules.WindowZones.Ui;

namespace Helm.Modules.WindowZones;

/// <summary>
/// The zones and their windows, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>):
/// the shared Avalonia <see cref="WindowZonesView"/> inside this WPF page. What only Windows has comes from here: the
/// monitors, the programs' icons and the shortcut recorder.
/// </summary>
public partial class WindowZonesContentPage : Page, IWindowZonesPageHost
{
    private readonly IMonitorService _monitors;

    public WindowZonesContentPage(WindowZonesViewModel viewModel, IMonitorService monitors)
    {
        _monitors = monitors;
        InitializeComponent();
        Host.Show(() => new WindowZonesView(viewModel, this));
    }

    public void Opened()
    {
    }

    public IReadOnlyList<MonitorInfo> Monitors() => _monitors.GetMonitors();

    /// <summary>The program's icon from Windows, handed to Avalonia as a picture; cached per .exe.</summary>
    public IImage? IconFor(string processPath) => ProgramIcons.For(processPath);

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
