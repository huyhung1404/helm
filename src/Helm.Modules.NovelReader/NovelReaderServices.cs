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
            // The AI name scan and suggested translations: Claude Code on this PC, through Helm's MCP tools.
            .AddSingleton<ClaudeCodeRunner>()
            .AddSingleton<Helm.Modules.NovelReader.Names.INameScanAgent, ClaudeCodeNameAgent>()
            .AddSingleton<Helm.Modules.NovelReader.Reports.ITranslationAgent, ClaudeCodeTranslationAgent>()
            .AddSingleton<WindowsAudioOutput>()
            .AddSingleton<IAudioOutput>(sp => sp.GetRequiredService<WindowsAudioOutput>())
            // A long press on a word in study mode says it in Mandarin, through a player of its own.
            .AddSingleton<IClipPlayer, WindowsClipPlayer>()
            // Xiaoxiao through the hidden Edge: reaches Microsoft's voices where a direct request is refused.
            .AddSingleton<IWordSpeaker>(sp => new EdgeWordSpeaker(sp.GetRequiredService<EdgeAnchor>()))
            .AddSingleton<WordPronouncer>()
            // Word classes for colouring subjects, verbs and objects under the mouse (jieba's list, built into this module).
            .AddSingleton(_ => new Grammar.WordClassDictionary(OpenWordClasses))
            // Scrape from web (Edge through DevTools), and Helm's own look for new chapters of web novels.
            .AddSingleton<NovelScraperService>()
            .AddSingleton<NovelScraperViewModel>()
            .AddSingleton<NovelSourceWatcher>()
            .AddNovelReaderCore()
            .AddHelmModule<NovelReaderModule, NovelReaderPage, NovelReaderViewModel>()
            .AddSingleton<NovelReaderContentPage>();

    internal static Stream OpenWordClasses() =>
        new System.IO.Compression.GZipStream(
            typeof(NovelReaderServices).Assembly.GetManifestResourceStream("Helm.Modules.NovelReader.word-classes.txt.gz")
                ?? throw new InvalidOperationException("The word classes are missing from the build."),
            System.IO.Compression.CompressionMode.Decompress);
}
