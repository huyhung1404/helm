using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Ai;

public static class AiCoreServices
{
    /// <summary>
    /// The API keys and what Helm's calls cost (both on Helm Sync), each feature's choice of model, the key checkers,
    /// the balance readers and Ask Helm's assistant; both apps call it.
    /// </summary>
    public static IServiceCollection AddHelmAi(this IServiceCollection services)
    {
        services.AddSyncedCollection(new SyncedCollectionOptions<AiKey> { Name = AiKeyStore.Collection, GuardDeletions = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<AiManagementKey> { Name = AiManagementKeys.Collection, GuardDeletions = true });
        services.AddSyncedLog<AiUsageEntry>(AiUsageBook.Collection);
        // Agent jobs between devices: urgent, so a turn reaches the PC (and its answer the phone) within a moment.
        services.AddSyncedCollection(new SyncedCollectionOptions<AiJobRecord> { Name = AgentJobs.JobsCollection, Urgent = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<AiWorkerRecord> { Name = AgentJobs.WorkersCollection, Urgent = true });
        services.AddSyncGroup("ai.", "AI keys and usage");
        services.AddSyncedSettings<AiFeatureSettings>(AiFeatureSettings.StoreId);
        services.AddSingleton(sp => new AiKeyStore(sp.GetRequiredService<ISyncedCollection<AiKey>>()));
        services.AddSingleton(sp => new AiUsageBook(sp.GetRequiredService<ISyncedLog<AiUsageEntry>>()));
        services.AddSingleton(sp => new AiManagementKeys(sp.GetRequiredService<ISyncedCollection<AiManagementKey>>(),
            sp.GetRequiredService<HelmPaths>(), sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<IAiKeyChecker>(_ => new ClaudeKeyChecker());
        services.AddSingleton<IAiKeyChecker>(_ => new GrokKeyChecker(AiHttp.Shared));
        services.AddSingleton<IAiBalanceReader>(_ => new GrokBalanceReader(AiHttp.Shared));
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
                new AiRouter(sp.GetServices<IAgentRoute>()));
        });
        // Text-only calls of the tools (translate a selection, analyse a word on the phone…), on the same router.
        services.AddSingleton<Helm.Core.Ai.IAiText>(sp => new AiTextService(sp.GetRequiredService<AiKeyStore>(),
            sp.GetRequiredService<ISyncedSettings<AiFeatureSettings>>(), sp.GetRequiredService<AiUsageBook>(),
            sp.GetServices<IAssistantChatFactory>(), new AiRouter(sp.GetServices<IAgentRoute>())));
        return services;
    }
}
