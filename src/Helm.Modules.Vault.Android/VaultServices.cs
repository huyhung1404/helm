using Helm.Core.Modules;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Platform;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vault;

/// <summary>The vault page: the module (for the Enable card), the vault screens and the settings, all shared view models.</summary>
public sealed class VaultPageViewModel(VaultModule module, VaultAppViewModel app, VaultSettingsViewModel settings)
{
    public VaultModule Module { get; } = module;

    public VaultAppViewModel App { get; } = app;

    public VaultSettingsViewModel Settings { get; } = settings;
}

public static class VaultServices
{
    public static IServiceCollection AddVaultModule(this IServiceCollection services)
    {
        // Android pieces first: AddVaultCore only fills in what is still missing.
        services.AddSingleton<IVaultDeviceUnlock, BiometricUnlock>();
        services.AddSingleton<IVaultBackupLocation, SafBackupLocation>();
        services.AddSingleton<IVaultPlatform, AndroidVaultPlatform>();
        services.AddVaultCore();
        services.AddSingleton<VaultPageViewModel>();
        return services.AddAndroidModule<VaultModule, VaultPage>();
    }
}
