using Avalonia.Controls;
using Avalonia.Media;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Modules.WindowZones.Ui;

namespace Helm.Modules.WindowZones;

/// <summary>
/// The zones and their windows, opened from the menu, Quick access and Home → Utilities: the shared
/// <see cref="WindowZonesView"/>. What only Windows has comes from here: the monitors, the programs' icons and the
/// shortcut recorder.
/// </summary>
internal sealed class WindowsWindowZonesPageHost : IWindowZonesPageHost
{
    private readonly IMonitorService _monitors;

    public WindowsWindowZonesPageHost(WindowZonesViewModel viewModel, IMonitorService monitors)
    {
        _monitors = monitors;
        View = new WindowZonesView(viewModel, this);
    }

    /// <summary>The page the shell shows.</summary>
    public Control View { get; }

    public void Opened()
    {
    }

    public IReadOnlyList<MonitorInfo> Monitors() => _monitors.GetMonitors();

    /// <summary>The program's icon from Windows, as an Avalonia picture; cached per .exe.</summary>
    public IImage? IconFor(string processPath) => ProgramIcons.For(processPath);

    public Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed)
    {
        var box = new Helm.Ui.Windows.HotkeyBox { Gesture = gesture, MinHeight = 40 };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == Helm.Ui.Windows.HotkeyBox.GestureProperty) changed(box.Gesture);
        };
        return box;
    }
}
