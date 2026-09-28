using Helm.Core;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

public partial class TrackerPage : ModulePageBase
{
    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public TrackerPage()
        : this(HelmAndroidServices.Current.GetRequiredService<TrackerModule>(), HelmAndroidServices.Current.GetRequiredService<TrackerViewModel>())
    {
    }

    public TrackerPage(TrackerModule module, TrackerViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
    }
}
