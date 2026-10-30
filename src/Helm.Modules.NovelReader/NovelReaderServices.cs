using Helm.Core;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.NovelReader;

public static class NovelReaderServices
{
    public static IServiceCollection AddNovelReaderModule(this IServiceCollection services) =>
        services
            // Windows voices (what reading falls back on before the VieNeu voice is downloaded) and the media player with
            // the system media controls. The VieNeu voice itself comes with the shared core.
            .AddSingleton<ISpeechEngine, WindowsSpeechEngine>()
            // The AI name scan and suggested translations: Claude Code on this PC, through Helm's MCP tools.
            .AddSingleton<Helm.Modules.NovelReader.Names.INameScanAgent, ClaudeCodeNameAgent>()
            .AddSingleton<Helm.Modules.NovelReader.Reports.ITranslationAgent, ClaudeCodeTranslationAgent>()
            .AddSingleton<WindowsAudioOutput>()
            .AddSingleton<IAudioOutput>(sp => sp.GetRequiredService<WindowsAudioOutput>())
            // A long press on a word in study mode says it in Mandarin, through a player of its own.
            .AddSingleton<IClipPlayer, WindowsClipPlayer>()
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
