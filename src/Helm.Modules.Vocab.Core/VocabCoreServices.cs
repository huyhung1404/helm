using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Vocab;

public static class VocabIds
{
    /// <summary>Module id on both apps: the settings file (vocab.json) and the enabled-state key.</summary>
    public const string ModuleId = "vocab";

    public const string DisplayName = "Vocabulary";

    public const string Description = "Learn English and Chinese words by their parts: an AI agent breaks each word you add into meaningful pieces, " +
        "and every piece opens into smaller ones. Words you forget come back more often.";
}

public static class VocabCoreServices
{
    /// <summary>
    /// The platform-neutral part of Vocabulary: its synced collections, the store, the quiz and the page view model. The
    /// platform module (Windows or Android) calls this from its own <c>AddVocabModule()</c>.
    /// </summary>
    public static IServiceCollection AddVocabCore(this IServiceCollection services)
    {
        // Words and parts are small records edited as a whole: the latest edit wins. Quiz answers are append-only, so
        // answering on two devices never overwrites a word's analysis.
        services.AddSyncedCollection(new SyncedCollectionOptions<VocabWord> { Name = VocabStore.WordsCollection });
        services.AddSyncedCollection(new SyncedCollectionOptions<VocabPart> { Name = VocabStore.PartsCollection });
        services.AddSyncedLog<VocabReview>(VocabStore.ReviewsCollection);
        services.AddSyncGroup("vocab.", VocabIds.DisplayName);
        services.AddSingleton(sp => new VocabStore(
            sp.GetRequiredService<ISyncedCollection<VocabWord>>(),
            sp.GetRequiredService<ISyncedCollection<VocabPart>>(),
            sp.GetRequiredService<ISyncedLog<VocabReview>>()));
        services.AddSingleton(sp => new VocabQuiz(sp.GetRequiredService<VocabStore>()));
        services.AddSingleton(sp => new VocabVoice(() => sp.GetService<Helm.Modules.NovelReader.Speech.WordPronouncer>(), sp.GetService<Helm.Core.Services.IProcessLauncher>()));
        // Quick Capture ("/v relative"), the share sheet and the phone's text selection add words through this.
        services.AddSingleton(sp => new VocabCaptureTarget(sp.GetRequiredService<VocabStore>()));
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget>(sp => sp.GetRequiredService<VocabCaptureTarget>());
        // The AI agents break words down through these (Helm's MCP server, Windows).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, VocabMcpTools>();
        services.AddSingleton(sp => new VocabSettingsViewModel(
            sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>().Get<VocabSettings>(VocabIds.ModuleId)));
        // The agent that breaks words down: Claude Code on Windows (IVocabAgent), none on the phone.
        services.AddSingleton(sp => new VocabAgentViewModel(sp.GetService<IVocabAgent>(), sp.GetRequiredService<VocabStore>(),
            sp.GetRequiredService<VocabSettingsViewModel>(), sp.GetRequiredService<Helm.Core.Services.IUiDispatcher>()));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<VocabViewModel>();
        return services;
    }
}
