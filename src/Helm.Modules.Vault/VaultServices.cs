using Helm.Core;
using Helm.Core.Modules;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Platform;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vault;

public static class VaultServices
{
    public static IServiceCollection AddVaultModule(this IServiceCollection services)
    {
        // Windows pieces first: AddVaultCore only fills in what is still missing.
        services.AddSingleton<IVaultDeviceUnlock, WindowsHelloUnlock>();
        services.AddSingleton<IVaultBackupLocation, FolderBackupLocation>();
        services.AddSingleton<SecureClipboard>();
        services.AddSingleton<IVaultPlatform, WindowsVaultPlatform>();
        services.AddSingleton<AutoTypeService>();
        // A tool that needs a vault secret while the vault is locked can open Vault for the password.
        services.AddSingleton<Helm.Core.Secrets.IVaultSecrets>(sp => new Helm.Modules.Vault.Items.VaultSecrets(
            sp.GetRequiredService<VaultSession>(), sp.GetRequiredService<Helm.Modules.Vault.Items.VaultStore>(),
            () => sp.GetRequiredService<Helm.Core.Services.IShellNavigation>().ShowPage(typeof(VaultContentPage))));
        services.AddVaultCore();
        // Settings page through AddHelmModule; the vault itself is the content page (navigation pane, Home tile).
        return services.AddHelmModule<VaultModule, VaultPage, VaultPageViewModel>()
            .AddSingleton<VaultContentViewModel>()
            .AddSingleton<VaultContentPage>()
            .AddSingleton<Helm.Core.Palette.IPaletteProvider, VaultPaletteProvider>();
    }
}
