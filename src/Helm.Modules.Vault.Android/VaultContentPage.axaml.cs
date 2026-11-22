using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Helm.Modules.Vault;

public partial class VaultContentPage : UserControl
{
    public VaultContentPage(VaultPageViewModel viewModel)
    {
        DataContext = viewModel;
        AvaloniaXamlLoader.Load(this);
        // Leaving the page forgets what was typed and hides what was shown, so nobody finds them there later. Not on
        // pause: the folder picker of "Restore from a backup" pauses Helm while the typed password is still needed.
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.App.ForgetTypedSecrets();
            viewModel.App.Items.HideRevealed();
        };
    }

    /// <summary>For the XAML previewer only.</summary>
    public VaultContentPage() => AvaloniaXamlLoader.Load(this);
}
