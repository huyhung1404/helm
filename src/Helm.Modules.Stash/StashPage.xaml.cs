using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.Stash;

/// <summary>Stash's settings (Home → Utilities). The stash is on <see cref="StashContentPage"/>.</summary>
public partial class StashPage : ModulePageBase
{
    private readonly IShellNavigation _navigation;

    public StashPage(StashModule module, StashViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void OpenStash_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(StashContentPage));
}
