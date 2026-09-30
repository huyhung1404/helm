using Helm.Core.Capture;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.WatchLater;

public static class WatchLaterCoreServices
{
    /// <summary>
    /// The platform-neutral part of Watch Later: its synced collection, the store, metadata lookup, thumbnails, the page
    /// view model and the Quick Capture target. The platform module (Windows or Android) calls this from its own
    /// <c>AddWatchLaterModule()</c>, after registering its own <see cref="IWatchDownloads"/> and metadata sources if it
    /// has them (Android gets <see cref="RemoteDownloads"/>).
    /// </summary>
    public static IServiceCollection AddWatchLaterCore(this IServiceCollection services)
    {
        services.AddSyncedCollection(WatchLaterStore.Options);
        services.AddSyncGroup("watch.", WatchLaterIds.DisplayName);
        services.AddSingleton(sp => new WatchLaterStore(sp.GetRequiredService<ISyncedCollection<WatchItem>>()));
        services.AddSingleton<HttpVideoMetadataSource>();
        services.AddSingleton<IVideoMetadataSource>(sp => sp.GetRequiredService<HttpVideoMetadataSource>());
        services.AddSingleton<MetadataResolver>();
        services.AddSingleton<ThumbnailCache>();
        services.TryAddSingleton<IWatchDownloads, RemoteDownloads>();
        services.AddSingleton<ICaptureTarget, WatchLaterCaptureTarget>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<WatchLaterViewModel>();
        return services;
    }
}
