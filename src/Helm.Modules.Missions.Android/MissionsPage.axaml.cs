using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

/// <summary>Missions' settings (Home → Utilities). The missions themselves are on <see cref="MissionsContentPage"/>.</summary>
public partial class MissionsPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public MissionsPage()
        : this(HelmAndroidServices.Current.GetRequiredService<MissionsModule>(), HelmAndroidServices.Current.GetRequiredService<MissionsViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public MissionsPage(MissionsModule module, MissionsViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(MissionsContentPage));
}
