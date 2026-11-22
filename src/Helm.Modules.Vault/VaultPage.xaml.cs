using Helm.Core.Ui;

namespace Helm.Modules.Vault;

public partial class VaultPage : ModulePageBase
{
    public VaultPage(VaultPageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Views.PageLeaveGuard.Attach(this, viewModel.Settings, viewModel.Settings.ForgetTypedSecrets);
    }
}
