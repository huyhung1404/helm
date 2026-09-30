using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.WatchLater;

public static class WatchLaterServices
{
    /// <summary>The phone does not download: <see cref="RemoteDownloads"/> (from the core) asks a PC.</summary>
    public static IServiceCollection AddWatchLaterModule(this IServiceCollection services) =>
        services
            .AddWatchLaterCore()
            .AddAndroidModule<WatchLaterModule, WatchLaterPage>()
            .AddTransient<WatchLaterContentPage>();
}
