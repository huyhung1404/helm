using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.Missions;

/// <summary>Missions' settings (Home → Utilities). The missions themselves are on <see cref="MissionsContentPage"/>.</summary>
public partial class MissionsPage : ModulePageBase
{
    private readonly IShellNavigation _navigation;

    public MissionsPage(MissionsModule module, MissionsViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(MissionsContentPage));
}
