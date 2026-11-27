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
    /// the balance readers, the features' prompts from the Skill Hub, the daily model lists and the features' text calls
    /// (<see cref="Helm.Core.Ai.IAiText"/>); both apps call it.
    /// </summary>
    public static IServiceCollection AddHelmAi(this IServiceCollection services)
    {
        services.AddSyncedCollection(new SyncedCollectionOptions<AiKey> { Name = AiKeyStore.Collection, GuardDeletions = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<AiManagementKey> { Name = AiManagementKeys.Collection, GuardDeletions = true });
        services.AddSyncedLog<AiUsageEntry>(AiUsageBook.Collection);
        // Agent jobs between devices (the phone's features' text calls to a PC's agents): urgent, so a job reaches the PC
        // (and its answer the phone) within a moment.
        services.AddSyncedCollection(new SyncedCollectionOptions<AiJobRecord> { Name = AgentJobs.JobsCollection, Urgent = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<AiWorkerRecord> { Name = AgentJobs.WorkersCollection, Urgent = true });
        // Each feature's prompt as fetched from the user's Skill Hub: a PC fetches, every device uses it.
        services.AddSyncedCollection(new SyncedCollectionOptions<AiPromptOverride> { Name = AiPrompts.Collection });
        // What the PC's automatic jobs wrote and when each last ran: the jobs are gone (0.95), their records still sync
        // as before until H4d decides about them. Nothing reads or writes them.
        services.AddSyncedCollection(new SyncedCollectionOptions<AiBriefRecord> { Name = LeftoverAiRecords.BriefsCollection, Urgent = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<AutoJobRunRecord> { Name = LeftoverAiRecords.RunsCollection });
        services.AddSyncGroup("ai.", "AI keys and usage");
        services.AddSyncedSettings<AiFeatureSettings>(AiFeatureSettings.StoreId);
        services.AddSingleton(sp => new AiKeyStore(sp.GetRequiredService<ISyncedCollection<AiKey>>()));
        services.AddSingleton(sp => new AiUsageBook(sp.GetRequiredService<ISyncedLog<AiUsageEntry>>(),
            logger: sp.GetService<ILoggerFactory>()?.CreateLogger<AiUsageBook>()));
        services.AddSingleton(sp => new AiManagementKeys(sp.GetRequiredService<ISyncedCollection<AiManagementKey>>(),
            sp.GetRequiredService<HelmPaths>(), sp.GetRequiredService<ISecretProtector>(), sync: sp.GetService<ISyncService>()));
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
                () => engine.DeviceId, engine, logger: sp.GetService<ILoggerFactory>()?.CreateLogger<SyncAgentRoute>());
        });
        services.AddSingleton<IAgentRoute>(sp => sp.GetRequiredService<SyncAgentRoute>());
        services.AddSingleton<IAssistantChatFactory>(_ => new ClaudeAssistantChatFactory());
        services.AddSingleton<IAssistantChatFactory>(_ => new GrokAssistantChatFactory(AiHttp.Shared));
        // AI → Features (PC) and General › AI features (phone): each feature's choice, the models of the tested keys.
        services.AddSingleton(sp => new AiFeaturesViewModel(sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(),
            sp.GetRequiredService<AiUsageBook>(),
            () => AssistantModelOption.Offered(sp.GetRequiredService<AiKeyStore>(), sp.GetServices<IAssistantChatFactory>()),
            sp.GetRequiredService<Helm.Core.Services.IUiDispatcher>(), sp.GetRequiredService<ISyncedCollection<AiWorkerRecord>>()));
        // Text-only calls of the tools (translate a selection, analyse a word, file a transaction, read a bank
        // notification…): agents first, the API last.
        services.AddSingleton<Helm.Core.Ai.IAiText>(sp => new AiTextService(sp.GetRequiredService<AiKeyStore>(),
            sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(), sp.GetRequiredService<AiUsageBook>(),
            sp.GetServices<IAssistantChatFactory>(), new AiRouter(sp.GetServices<IAgentRoute>())));
        return services;
    }
}
