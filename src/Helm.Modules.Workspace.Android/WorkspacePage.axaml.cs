using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Workspace;

/// <summary>Workspace Controller's settings (Home → Utilities). The control screen is <see cref="WorkspaceContentPage"/>.</summary>
public partial class WorkspacePage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public WorkspacePage()
        : this(HelmAndroidServices.Current.GetRequiredService<WorkspaceModule>(), HelmAndroidServices.Current.GetRequiredService<WorkspaceRemoteViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public WorkspacePage(WorkspaceModule module, WorkspaceRemoteViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        Module = module;
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(WorkspaceContentPage));
}
