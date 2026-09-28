using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Helm.Modules.Vault;

public partial class VaultContentPage : UserControl
{
    public VaultContentPage(VaultPageViewModel viewModel)
    {
        DataContext = viewModel;
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>For the XAML previewer only.</summary>
    public VaultContentPage() => AvaloniaXamlLoader.Load(this);
}
