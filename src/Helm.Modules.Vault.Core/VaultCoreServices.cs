using Helm.Core.Sync;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Vault;

public static class VaultCoreServices
{
    /// <summary>
    /// The platform-neutral vault: its two synced collections, the session and the item store. The platform
    /// registers its <see cref="IVaultDeviceUnlock"/> (Windows Hello, Android biometric) before or after; without one,
    /// quick unlock is off.
    /// </summary>
    public static IServiceCollection AddVaultCore(this IServiceCollection services)
    {
        services.AddSyncedCollection(VaultStore.Options);
        // Last writer wins is safe here: every version of a vault's keyring wraps the same vault key.
        services.AddSyncedCollection(new SyncedCollectionOptions<VaultKeyringData>
        {
            Name = VaultSession.KeyringCollection,
            ConflictPolicy = SyncConflictPolicy.LastWriterWins,
        });
        // One record per vault and recovery key; records never change, so there is nothing to conflict on.
        services.AddSyncedCollection(new SyncedCollectionOptions<VaultKitConfirmation> { Name = VaultSession.KitCollection });
        // One record per device: when it last made a good backup (one device with a backup folder is enough).
        services.AddSyncedCollection(new SyncedCollectionOptions<VaultBackupMark> { Name = VaultBackupService.MarksCollection });
        services.AddSyncGroup("vault.", "Vault");
        services.TryAddSingleton<IVaultDeviceUnlock, NoDeviceUnlock>();
        services.AddSingleton<VaultSession>();
        services.AddSingleton<VaultStore>();
        services.AddSingleton<VaultFiles>();
        services.TryAddSingleton<IVaultBackupLocation, FolderBackupLocation>();
        services.AddSingleton<VaultBackupService>();
        // Screens shared by both apps; the platform registers IVaultPlatform and IUiDispatcher.
        services.AddSingleton<ViewModels.VaultAppViewModel>();
        services.AddSingleton<ViewModels.VaultSettingsViewModel>();
        return services;
    }
}
