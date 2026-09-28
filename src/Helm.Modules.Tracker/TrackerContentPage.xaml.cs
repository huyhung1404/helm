using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the
/// tool is off the lists stay visible but read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class TrackerContentPage : Page
{
    private readonly TrackerModule _module;

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel)
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
