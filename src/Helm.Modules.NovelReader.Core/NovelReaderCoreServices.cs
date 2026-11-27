using Helm.Core.Sync;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Reports;
using Helm.Modules.NovelReader.Speech;
using Helm.Modules.NovelReader.Speech.VieNeu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        services.AddNovelReaderAgentCore();
        services.AddSingleton<DictionaryLibrary>();
        // The VieNeu voices, running in Helm (downloaded once, on the user's word); platforms add their own engines
        // (Windows voices, the phone's) as ISpeechEngine too.
        services.AddSingleton(sp => new VieNeuInstaller(
            new VieNeuFiles(Path.Combine(sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>().Paths.Root, "models", "vieneu-v3-turbo")),
            sp.GetRequiredService<ILogger<VieNeuInstaller>>()));
        services.AddSingleton<VieNeuSpeechEngine>();
        services.AddSingleton<ISpeechEngine>(sp => sp.GetRequiredService<VieNeuSpeechEngine>());
        // Edge's read-aloud service, only for Xiaoxiao saying a Chinese word pressed in study mode (no reading voices).
        services.AddSingleton<ISpeechEngine, EdgeSpeechEngine>();
        services.AddSingleton(sp => new SpeechCatalog(sp.GetServices<ISpeechEngine>()));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<NovelReaderViewModel>();
        return services;
    }

    /// <summary>
    /// What an agent reaches without the reader: the synced collections and the "novel." sync group, the novels' store
    /// (their texts are files, read only when asked), the reports of wrong translations, and the tools for agents.
    /// <see cref="AddNovelReaderCore"/> adds the dictionaries, the voices and the page; Fairy's connector stops here (no
    /// dictionaries: a report comes without what Helm shows for it today, and names are looked for in Helm).
    /// </summary>
    public static IServiceCollection AddNovelReaderAgentCore(this IServiceCollection services)
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
        services.AddSingleton(sp => new TranslationReportStore(sp.GetRequiredService<ISyncedCollection<TranslationReport>>()));
        // Tools for an AI (MCP: Helm on the PC, Fairy's connector).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider>(sp =>
            new NovelReaderMcpTools(sp.GetRequiredService<NovelStore>(), sp.GetService<DictionaryLibrary>()));
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider>(sp =>
            new TranslationReportMcpTools(sp.GetRequiredService<TranslationReportStore>(), sp.GetRequiredService<NovelStore>(), sp.GetService<DictionaryLibrary>()));
        return services;
    }
}
