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
        services.AddVaultCore();
        return services.AddHelmModule<VaultModule, VaultPage, VaultPageViewModel>()
            .AddSingleton<VaultWindowHost>()
            .AddSingleton<IModuleLauncher, VaultLauncher>();
    }
}
