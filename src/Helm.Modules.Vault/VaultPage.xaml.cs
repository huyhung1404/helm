using Helm.Core.Ui;

namespace Helm.Modules.Vault;

public partial class VaultPage : ModulePageBase
{
    public VaultPage(VaultPageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
