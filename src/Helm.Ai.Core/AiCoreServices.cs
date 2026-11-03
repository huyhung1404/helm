using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Ai;

public static class AiCoreServices
{
    /// <summary>The API keys (on Helm Sync), the key checkers and the balance readers; both apps call it.</summary>
    public static IServiceCollection AddHelmAi(this IServiceCollection services)
    {
        services.AddSyncedCollection(new SyncedCollectionOptions<AiKey> { Name = AiKeyStore.Collection, GuardDeletions = true });
        services.AddSyncGroup("ai.", "AI keys");
        services.AddSingleton(sp => new AiKeyStore(sp.GetRequiredService<ISyncedCollection<AiKey>>()));
        services.AddSingleton(sp => new AiManagementKeys(sp.GetRequiredService<HelmPaths>(), sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<IAiKeyChecker>(_ => new ClaudeKeyChecker());
        services.AddSingleton<IAiKeyChecker>(_ => new GrokKeyChecker(AiHttp.Shared));
        services.AddSingleton<IAiBalanceReader>(_ => new GrokBalanceReader(AiHttp.Shared));
        return services;
    }
}
