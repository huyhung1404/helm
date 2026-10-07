using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Stash;

/// <summary>Stash's settings (Home → Utilities). The stash is on <see cref="StashContentPage"/>.</summary>
public partial class StashPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public StashPage()
        : this(HelmAndroidServices.Current.GetRequiredService<StashModule>(), HelmAndroidServices.Current.GetRequiredService<StashViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public StashPage(StashModule module, StashViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
    }

    private void OpenStash_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(StashContentPage));
}
