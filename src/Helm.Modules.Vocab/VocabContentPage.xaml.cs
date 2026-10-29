using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the words stay visible but read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class VocabContentPage : Page
{
    private readonly VocabModule _module;

    public VocabContentPage(VocabModule module, VocabViewModel viewModel)
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
