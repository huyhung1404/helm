using Helm.Core.Modules;
using Helm.Core.Services;
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
        // Android pieces first: AddVaultCore only fills in what is still missing.
        services.AddSingleton<IVaultDeviceUnlock, BiometricUnlock>();
        services.AddSingleton<IVaultBackupLocation, SafBackupLocation>();
        services.AddSingleton<IVaultPlatform, AndroidVaultPlatform>();
        // A tool that needs a vault secret while the vault is locked (SSH) can open Vault for the password.
        services.AddSingleton<Helm.Core.Secrets.IVaultSecrets>(sp => new Helm.Modules.Vault.Items.VaultSecrets(
            sp.GetRequiredService<VaultSession>(), sp.GetRequiredService<Helm.Modules.Vault.Items.VaultStore>(),
            () => sp.GetRequiredService<IShellNavigation>().ShowPage(typeof(VaultContentPage))));
        services.AddVaultCore();
        return services.AddAndroidModule<VaultModule, VaultContentPage>();
    }
}
