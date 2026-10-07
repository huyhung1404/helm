using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Scratch;

public static class ScratchCoreServices
{
    /// <summary>
    /// The platform-neutral part of Scratch: its synced collection, the store, the importer and the page view model. The platform
    /// module (Windows or Android) calls this from its own <c>AddScratchModule()</c>, after registering its
    /// <see cref="IScratchPlatform"/>.
    /// </summary>
    public static IServiceCollection AddScratchCore(this IServiceCollection services)
    {
        services.AddSyncedCollection(ScratchStore.Options);
        services.AddSyncGroup("scratch.", ScratchIds.DisplayName);
        services.AddSingleton(sp => new ScratchStore(sp.GetRequiredService<ISyncedCollection<ScratchItem>>(), sp.GetRequiredService<BlobStore>()));
        services.AddSingleton<ScratchImporter>();
        services.AddSyncedCollection(ScratchClipboardRelay.Options);
        services.AddSingleton<ScratchClipboardRelay>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<ScratchViewModel>();
        return services;
    }
}
