using Avalonia.Markup.Xaml;
using Helm.Core.Ui;

namespace Helm.Modules.Vault;

public partial class VaultPage : ModulePageBase
{
    public VaultPage(VaultPageViewModel viewModel)
    {
        DataContext = viewModel;
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>For the XAML previewer only.</summary>
    public VaultPage() => AvaloniaXamlLoader.Load(this);
}
