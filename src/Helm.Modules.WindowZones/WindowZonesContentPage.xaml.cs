using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.WindowZones;

/// <summary>
/// The zones and their windows, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>):
/// open or close a zone, see which windows are in it, name it, pick its shortcut, draw zones. While the tool is off
/// the zones stay visible but read-only.
/// </summary>
public partial class WindowZonesContentPage : Page
{
    private readonly WindowZonesModule _module;

    public WindowZonesContentPage(WindowZonesModule module, WindowZonesViewModel viewModel)
    {
        _module = module;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscription lives as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
