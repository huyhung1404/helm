using Helm.Core.Ui;

namespace Helm.Modules.Workspace;

/// <summary>Workspace Controller on the PC: the host's status and options. The phone steers the sessions.</summary>
public partial class WorkspacePage : ModulePageBase
{
    public WorkspacePage(WorkspaceViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
