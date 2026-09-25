using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Sync;

public static class SyncServices
{
    /// <summary>
    /// Registers the local replica, the engine and the HTTPS transport. Until this device has a token and a master
    /// key, everything works locally and sync is a no-op.
    /// </summary>
    public static IServiceCollection AddHelmSync(this IServiceCollection services, HelmPaths paths)
    {
        services.AddSingleton(_ => new SyncDatabase(paths.SyncDatabaseFile));
        services.TryAddSingleton<ISyncCredentialStore>(_ => new DpapiSyncCredentialStore(paths.SyncCredentialsFile));
        services.TryAddSingleton(_ => new SyncApiClient());
        services.TryAddSingleton<ISyncTransport>(sp => new HttpSyncTransport(
            sp.GetRequiredService<ISyncCredentialStore>(), sp.GetRequiredService<SyncApiClient>()));
        services.TryAddSingleton<IMasterKeyStore>(_ => new DpapiMasterKeyStore(paths.SyncKeyFile));
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddSingleton<SyncSetupService>();
        return services;
    }

    /// <summary>Registers <see cref="ISyncedCollection{T}"/> for a module.</summary>
    public static IServiceCollection AddSyncedCollection<T>(this IServiceCollection services, SyncedCollectionOptions<T> options)
        where T : class
    {
        // The descriptor is known to the engine from startup, so conflicts get the right policy even before the
        // module first resolves its collection.
        services.AddSingleton(options.ToDescriptor());
        services.AddSingleton<ISyncedCollection<T>>(sp => new SyncedCollection<T>(
            sp.GetRequiredService<SyncEngine>(), options, Logger(sp, options.Name)));
        return services;
    }

    /// <summary>Registers <see cref="ISyncedLog{T}"/> (append-only, never conflicts).</summary>
    public static IServiceCollection AddSyncedLog<T>(this IServiceCollection services, string name, int schemaVersion = 1)
        where T : class
    {
        services.AddSingleton(SyncedLog<T>.Options(name, schemaVersion).ToDescriptor());
        services.AddSingleton<ISyncedLog<T>>(sp => new SyncedLog<T>(sp.GetRequiredService<SyncEngine>(), name, schemaVersion));
        return services;
    }

    /// <summary>Registers <see cref="ISyncedSettings{T}"/>, stored in collection "settings.&lt;storeId&gt;".</summary>
    public static IServiceCollection AddSyncedSettings<T>(this IServiceCollection services, string storeId)
        where T : class, IVersionedSettings, new()
    {
        services.AddSingleton(SyncedSettings<T>.Descriptor(storeId));
        services.AddSingleton<ISyncedSettings<T>>(sp => new SyncedSettings<T>(
            sp.GetRequiredService<SyncEngine>(), storeId, Logger(sp, SyncedSettings<T>.CollectionName(storeId))));
        return services;
    }

    private static ILogger? Logger(IServiceProvider sp, string collection) =>
        sp.GetService<ILoggerFactory>()?.CreateLogger($"Sync.{collection}");
}
