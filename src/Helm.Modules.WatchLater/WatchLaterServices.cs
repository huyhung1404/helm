using Helm.Core;
using Helm.Core.Palette;
using Helm.Modules.WatchLater.Player;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.WatchLater;

public static class WatchLaterServices
{
    public static IServiceCollection AddWatchLaterModule(this IServiceCollection services) =>
        services
            // Windows downloads with yt-dlp (registered before the core, which falls back to asking a PC).
            .AddSingleton<YtDlpTools>()
            .AddSingleton<PcDownloads>()
            .AddSingleton<IWatchDownloads>(sp => sp.GetRequiredService<PcDownloads>())
            .AddSingleton<IVideoMetadataSource, YtDlpMetadataSource>()
            // Play opens Helm's own player window.
            .AddSingleton<PlayerService>()
            .AddSingleton<IVideoPlayer>(sp => sp.GetRequiredService<PlayerService>())
            .AddWatchLaterCore()
            .AddHelmModule<WatchLaterModule, WatchLaterPage, WatchLaterViewModel>()
            .AddSingleton<WatchLaterToolsViewModel>()
            .AddSingleton<WatchLaterContentPage>()
            .AddSingleton<IPaletteProvider, WatchLaterPaletteProvider>();
}
