using Helm.Ai.Assistant;
using Helm.Ai.Prompts;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Ai;

public static class AiCoreServices
{
    /// <summary>
    /// The API keys and what Helm's calls cost (both on Helm Sync), each feature's choice of model, the key checkers,
    /// the balance readers, the features' prompts from the Skill Hub, the daily model lists and Ask Helm's assistant;
    /// both apps call it.
    /// </summary>
    public static IServiceCollection AddHelmAi(this IServiceCollection services)
    {
        services.AddSyncedCollection(new SyncedCollectionOptions<AiKey> { Name = AiKeyStore.Collection, GuardDeletions = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<AiManagementKey> { Name = AiManagementKeys.Collection, GuardDeletions = true });
        services.AddSyncedLog<AiUsageEntry>(AiUsageBook.Collection);
        // Agent jobs between devices: urgent, so a turn reaches the PC (and its answer the phone) within a moment.
        services.AddSyncedCollection(new SyncedCollectionOptions<AiJobRecord> { Name = AgentJobs.JobsCollection, Urgent = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<AiWorkerRecord> { Name = AgentJobs.WorkersCollection, Urgent = true });
        // Each feature's prompt as fetched from the user's Skill Hub: a PC fetches, every device uses it.
        services.AddSyncedCollection(new SyncedCollectionOptions<AiPromptOverride> { Name = AiPrompts.Collection });
        // What the PC's automatic jobs wrote (urgent: the phone hears of a brief at once) and when each last ran.
        services.AddSyncedCollection(new SyncedCollectionOptions<Auto.AiBrief> { Name = Auto.AiBriefBook.Collection, Urgent = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<Auto.AutoJobRun> { Name = Auto.AutoJobRuns.Collection });
        services.AddSyncGroup("ai.", "AI keys and usage");
        services.AddSyncedSettings<AiFeatureSettings>(AiFeatureSettings.StoreId);
        services.AddSingleton(sp => new AiKeyStore(sp.GetRequiredService<ISyncedCollection<AiKey>>()));
        services.AddSingleton(sp => new Auto.AiBriefBook(sp.GetRequiredService<ISyncedCollection<Auto.AiBrief>>()));
        services.AddSingleton(sp => new Auto.AutoJobRuns(sp.GetRequiredService<ISyncedCollection<Auto.AutoJobRun>>()));
        services.AddSingleton(sp => new Auto.AiBriefAlerts(sp.GetRequiredService<Auto.AiBriefBook>(),
            sp.GetRequiredService<ISettingsStoreFactory>().Get<Auto.AiBriefAlertState>(Auto.AiBriefAlertState.StoreId)));
        // Ask Helm reads the briefs and sets the jobs; undoing a job's change runs the tool's own Undo.
        services.AddSingleton<IMcpToolProvider>(sp => new Auto.AiBriefTools(sp.GetRequiredService<Auto.AiBriefBook>(),
            sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(), () => sp.GetServices<IMcpToolProvider>()));
        services.AddSingleton(sp => new AiUsageBook(sp.GetRequiredService<ISyncedLog<AiUsageEntry>>()));
        services.AddSingleton(sp => new AiManagementKeys(sp.GetRequiredService<ISyncedCollection<AiManagementKey>>(),
            sp.GetRequiredService<HelmPaths>(), sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<IAiKeyChecker>(_ => new ClaudeKeyChecker());
        services.AddSingleton<IAiKeyChecker>(_ => new GrokKeyChecker(AiHttp.Shared));
        services.AddSingleton<IAiBalanceReader>(_ => new GrokBalanceReader(AiHttp.Shared));
        services.AddSingleton(sp => new AiModelRefresher(sp.GetRequiredService<AiKeyStore>(), sp.GetServices<IAiKeyChecker>(),
            sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(), sp.GetService<ILoggerFactory>()?.CreateLogger<AiModelRefresher>()));
        services.AddSingleton(sp => new AiPrompts(sp.GetRequiredService<ISyncedCollection<AiPromptOverride>>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<AiPrompts>()));
        services.AddSingleton<IAiPrompts>(sp => sp.GetRequiredService<AiPrompts>());
        services.AddSingleton(sp => new SkillHubSecrets(sp.GetRequiredService<HelmPaths>(), sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<IAiPromptSource>(sp =>
        {
            var secrets = sp.GetRequiredService<SkillHubSecrets>();
            var settings = sp.GetRequiredService<ISettingsStoreFactory>().Get<SkillHubSettings>(SkillHubSettings.StoreId);
            return new SkillHubPromptSource(SkillHubPromptSource.CreateClient(), () => secrets.Connection(settings));
        });
        services.AddSingleton(sp => new AiPromptRefresher(sp.GetRequiredService<AiPrompts>(), sp.GetRequiredService<IAiPromptSource>(),
            logger: sp.GetService<ILoggerFactory>()?.CreateLogger<AiPromptRefresher>()));
        services.AddSingleton(sp =>
        {
            var engine = sp.GetRequiredService<SyncEngine>();
            return new SyncAgentRoute(sp.GetRequiredService<ISyncedCollection<AiJobRecord>>(), sp.GetRequiredService<ISyncedCollection<AiWorkerRecord>>(),
                () => engine.DeviceId, engine);
        });
        services.AddSingleton<IAgentRoute>(sp => sp.GetRequiredService<SyncAgentRoute>());
        services.AddSingleton<IAssistantChatFactory>(_ => new ClaudeAssistantChatFactory());
        services.AddSingleton<IAssistantChatFactory>(_ => new GrokAssistantChatFactory(AiHttp.Shared));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStoreFactory>();
            // A tool's tools are offered while the tool is on (read from the saved settings: Ask Helm runs without the
            // app's screen, before the module registry starts).
            bool IsOn(string? moduleId) => moduleId is null
                || !settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules.TryGetValue(moduleId, out var on) || on;
            return new HelmAssistant(sp.GetRequiredService<AiKeyStore>(), sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(),
                sp.GetRequiredService<AiUsageBook>(), sp.GetServices<IMcpToolProvider>(), sp.GetServices<IAssistantChatFactory>(), IsOn,
                new AiRouter(sp.GetServices<IAgentRoute>()), prompts: sp.GetRequiredService<IAiPrompts>());
        });
        // AI → Features (PC) and General › AI features (phone): each feature's choice, the models of the tested keys.
        services.AddSingleton(sp => new AiFeaturesViewModel(sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(),
            sp.GetRequiredService<AiUsageBook>(), () => sp.GetRequiredService<HelmAssistant>().Models(),
            sp.GetRequiredService<Helm.Core.Services.IUiDispatcher>(), sp.GetRequiredService<ISyncedCollection<AiWorkerRecord>>()));
        // One answer, no tools (file a transaction, read a bank notification): agents first, the API last.
        services.AddSingleton(sp => new AiOneShot(sp.GetRequiredService<AiKeyStore>(), sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(),
            sp.GetRequiredService<AiUsageBook>(), sp.GetServices<IAssistantChatFactory>(), new AiRouter(sp.GetServices<IAgentRoute>())));
        // Text-only calls of the tools (translate a selection, analyse a word on the phone…), on the same router.
        services.AddSingleton<Helm.Core.Ai.IAiText>(sp => new AiTextService(sp.GetRequiredService<AiKeyStore>(),
            sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(), sp.GetRequiredService<AiUsageBook>(),
            sp.GetServices<IAssistantChatFactory>(), new AiRouter(sp.GetServices<IAgentRoute>())));
        return services;
    }
}
