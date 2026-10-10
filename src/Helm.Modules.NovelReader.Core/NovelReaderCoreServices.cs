using Helm.Core.Sync;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.NovelReader;

public static class NovelReaderCoreServices
{
    /// <summary>
    /// The platform-neutral part of Novel Reader: its synced collections (novels, saved names and meanings, progress,
    /// dictionary files) under the "novel." sync group, the store, the dictionaries and the page view model. The platform module
    /// calls this from its own <c>AddNovelReaderModule()</c>, after registering its <see cref="IAudioOutput"/> and its own speech engines.
    /// </summary>
    public static IServiceCollection AddNovelReaderCore(this IServiceCollection services)
    {
        services.AddSyncedCollection(NovelStore.BookOptions);
        services.AddSyncedCollection(NovelStore.EntryOptions);
        services.AddSyncedCollection(NovelStore.ProgressOptions);
        services.AddSyncedCollection(DictionaryLibrary.Options);
        services.AddSyncGroup("novel.", NovelReaderIds.DisplayName);
        services.AddSingleton(sp => new NovelStore(
            sp.GetRequiredService<ISyncedCollection<NovelBook>>(),
            sp.GetRequiredService<ISyncedCollection<NovelEntry>>(),
            sp.GetRequiredService<ISyncedCollection<NovelProgress>>(),
            sp.GetRequiredService<BlobStore>()));
        services.AddSingleton<DictionaryLibrary>();
        // Tools for an AI on this PC (MCP, Windows only: other platforms never ask for them).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, NovelReaderMcpTools>();
        // The online HoaiMy voice; platforms add their own engines (Windows voices) as ISpeechEngine too.
        services.AddSingleton<ISpeechEngine, EdgeSpeechEngine>();
        services.AddSingleton(sp => new SpeechCatalog(sp.GetServices<ISpeechEngine>()));
        // Downloaded sentences, on this device only (cache/novel-reader/audio).
        services.AddSingleton(sp => new AudioCache(Path.Combine(
            sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>().Paths.Root, "cache", NovelReaderIds.ModuleId, "audio")));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<NovelReaderViewModel>();
        return services;
    }
}
