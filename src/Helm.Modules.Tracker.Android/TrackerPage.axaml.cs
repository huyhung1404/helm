using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

/// <summary>Tracker's settings (Home → Utilities). The lists are on <see cref="TrackerContentPage"/>.</summary>
public partial class TrackerPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public TrackerPage()
        : this(HelmAndroidServices.Current.GetRequiredService<TrackerModule>(), HelmAndroidServices.Current.GetRequiredService<TrackerViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public TrackerPage(TrackerModule module, TrackerViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
    }

    private void OpenTracker_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(TrackerContentPage));
}
