using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.Workspace;

/// <summary>Workspace Controller's settings (Home → Utilities). What is connected is on <see cref="WorkspaceContentPage"/>.</summary>
public partial class WorkspacePage : ModulePageBase
{
    private readonly IShellNavigation _navigation;

    public WorkspacePage(WorkspaceViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(WorkspaceContentPage));
}
