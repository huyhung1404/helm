using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.NovelReader;

public static class NovelReaderServices
{
    public static IServiceCollection AddNovelReaderModule(this IServiceCollection services) =>
        services
            // Windows voices (offline fallback) and the media player with the system media controls.
            .AddSingleton<ISpeechEngine, WindowsSpeechEngine>()
            // VieNeu-TTS voices on this PC, through its local server that Helm starts and stops.
            // HoaiMy as Microsoft Edge reads it, through a hidden Edge.
            .AddSingleton<EdgeAnchor>()
            .AddSingleton<ISpeechEngine>(sp => new EdgeAnchorEngine(sp.GetRequiredService<EdgeAnchor>()))
            .AddSingleton<VieNeuServer>()
            .AddSingleton<ILocalVoiceServer>(sp => sp.GetRequiredService<VieNeuServer>())
            .AddSingleton<ISpeechEngine>(sp => new LocalSpeechEngine(sp.GetRequiredService<ILocalVoiceServer>(),
                Path.Combine(sp.GetRequiredService<ISettingsStoreFactory>().Paths.ModuleDataDirectory(NovelReaderIds.ModuleId), "local-voices.json"),
                sp.GetRequiredService<ILogger<LocalSpeechEngine>>()))
            // The AI name scan: Claude Code on this PC, through Helm's MCP tools.
            .AddSingleton<Helm.Modules.NovelReader.Names.INameScanAgent, ClaudeCodeNameAgent>()
            .AddSingleton<WindowsAudioOutput>()
            .AddSingleton<IAudioOutput>(sp => sp.GetRequiredService<WindowsAudioOutput>())
            .AddNovelReaderCore()
            .AddHelmModule<NovelReaderModule, NovelReaderPage, NovelReaderViewModel>()
            .AddSingleton<NovelReaderContentPage>();
}
