using System.Collections.Concurrent;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
    private readonly ConcurrentDictionary<string, IImage?> _icons = new(StringComparer.OrdinalIgnoreCase);

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
    public IImage? IconFor(string processPath) => _icons.GetOrAdd(processPath, Load);

    public Avalonia.Controls.Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed)
    {
        var box = new Helm.Ui.Windows.HotkeyBox { Gesture = gesture, MinHeight = 40 };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == Helm.Ui.Windows.HotkeyBox.GestureProperty) changed(box.Gesture);
        };
        return box;
    }

    private static IImage? Load(string path)
    {
        if (Helm.Core.Desktop.ShellIcons.ForFile(path) is not BitmapSource icon) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(icon));
        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        return new Avalonia.Media.Imaging.Bitmap(png);
    }
}
