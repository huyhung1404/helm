using Avalonia.Controls;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Helm.Modules.Vault.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault;

/// <summary>
/// The vault, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the
/// shared <see cref="VaultView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>), plus what
/// only Android does: the autofill settings card and the menu button (the page has no app bar of the shell's).
/// </summary>
public sealed class VaultContentPage : UserControl, IFullPage
{
    public VaultContentPage(VaultModule module, VaultAppViewModel app, VaultSettingsViewModel settings)
    {
        Content = new VaultView(module, app, settings, new AndroidHost());
    }

    /// <summary>What only Android adds to the shared page.</summary>
    private sealed class AndroidHost : IVaultPageHost
    {
        public Control? CreatePlatformSettings() => new VaultAutofillCard();

        public bool CanLockWithSystem => false;
    }
}
