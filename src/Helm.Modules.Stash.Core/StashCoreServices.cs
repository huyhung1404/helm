using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Stash;

public static class StashCoreServices
{
    /// <summary>
    /// The platform-neutral part of Stash: its synced collection, the store, the importer and the page view model. The platform
    /// module (Windows or Android) calls this from its own <c>AddStashModule()</c>, after registering its
    /// <see cref="IStashPlatform"/>.
    /// </summary>
    public static IServiceCollection AddStashCore(this IServiceCollection services)
    {
        services.AddSyncedCollection(StashStore.Options);
        services.AddSyncGroup("stash.", StashIds.DisplayName);
        services.AddSingleton(sp => new StashStore(sp.GetRequiredService<ISyncedCollection<StashItem>>(), sp.GetRequiredService<BlobStore>()));
        services.AddSingleton<StashImporter>();
        services.AddSyncedCollection(StashClipboardRelay.Options);
        services.AddSingleton<StashClipboardRelay>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<StashViewModel>();
        return services;
    }
}
