using Helm.Core.Modules;
using Helm.Modules.NovelReader.Playback;
using Helm.Modules.NovelReader.Platform;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.NovelReader;

public static class NovelReaderServices
{
    public static IServiceCollection AddNovelReaderModule(this IServiceCollection services) =>
        services
            .AddSingleton<INovelPlatform, AndroidNovelPlatform>()
            // The phone's own voices (Google's Vietnamese ones, offline); the shared core adds HoaiMy online.
            .AddSingleton<AndroidTtsEngine>()
            .AddSingleton<ISpeechEngine>(sp => sp.GetRequiredService<AndroidTtsEngine>())
            // The player, with the background service, its notification and the lock-screen controls.
            .AddSingleton<AndroidAudioOutput>()
            .AddSingleton<IAudioOutput>(sp => sp.GetRequiredService<AndroidAudioOutput>())
            .AddNovelReaderCore()
            .AddAndroidModule<NovelReaderModule, NovelReaderPage>()
            .AddTransient<NovelReaderContentPage>();
}
