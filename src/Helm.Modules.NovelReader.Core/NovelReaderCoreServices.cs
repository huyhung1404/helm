using Helm.Core.Sync;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Reports;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.NovelReader;

public static class NovelReaderCoreServices
{
    /// <summary>
    /// The platform-neutral part of Novel Reader: its synced collections (novels, saved names and meanings, progress,
    /// dictionary files, reports of wrong translations) under the "novel." sync group, the stores, the dictionaries and
    /// the page view model. The platform module calls this from its own <c>AddNovelReaderModule()</c>, after registering its <see cref="IAudioOutput"/> and its own speech engines.
    /// </summary>
    public static IServiceCollection AddNovelReaderCore(this IServiceCollection services)
    {
        services.AddSyncedCollection(NovelStore.BookOptions);
        services.AddSyncedCollection(NovelStore.EntryOptions);
        services.AddSyncedCollection(NovelStore.ProgressOptions);
        services.AddSyncedCollection(DictionaryLibrary.Options);
        services.AddSyncedCollection(TranslationReportStore.Options);
        services.AddSyncGroup("novel.", NovelReaderIds.DisplayName);
        services.AddSingleton(sp => new NovelStore(
            sp.GetRequiredService<ISyncedCollection<NovelBook>>(),
            sp.GetRequiredService<ISyncedCollection<NovelEntry>>(),
            sp.GetRequiredService<ISyncedCollection<NovelProgress>>(),
            sp.GetRequiredService<BlobStore>()));
        services.AddSingleton<DictionaryLibrary>();
        services.AddSingleton(sp => new TranslationReportStore(sp.GetRequiredService<ISyncedCollection<TranslationReport>>()));
        // Tools for an AI on this PC (MCP, Windows only: other platforms never ask for them).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, NovelReaderMcpTools>();
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, TranslationReportMcpTools>();
        // The online HoaiMy voice; platforms add their own engines (Windows voices) as ISpeechEngine too.
        services.AddSingleton<ISpeechEngine, EdgeSpeechEngine>();
        services.AddSingleton(sp => new SpeechCatalog(sp.GetServices<ISpeechEngine>()));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<NovelReaderViewModel>();
        return services;
    }
}
