using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Missions;

public static class MissionsIds
{
    /// <summary>Module id on both apps: the settings file (missions.json) and the enabled-state key.</summary>
    public const string ModuleId = "missions";

    public const string DisplayName = "Missions";

    public const string Description = "Reach a goal one step at a time: import a roadmap written by an AI, work through it in order, and see every step's dates and your pace.";
}

public static class MissionsCoreServices
{
    /// <summary>
    /// The platform-neutral part of Missions: its three synced collections, the store, Claude's tools and the page view
    /// model. The platform module (Windows or Android) calls this from its own <c>AddMissionsModule()</c>.
    /// </summary>
    public static IServiceCollection AddMissionsCore(this IServiceCollection services)
    {
        // Missions and steps are small records edited as a whole: the latest edit wins. History is append-only.
        services.AddSyncedCollection(new SyncedCollectionOptions<Mission> { Name = MissionsStore.MissionsCollection });
        services.AddSyncedCollection(new SyncedCollectionOptions<MissionStep> { Name = MissionsStore.StepsCollection });
        services.AddSyncedLog<MissionEvent>(MissionsStore.HistoryCollection);
        services.AddSyncGroup("missions.", MissionsIds.DisplayName);
        services.AddSingleton(sp => new MissionsStore(
            sp.GetRequiredService<ISyncedCollection<Mission>>(),
            sp.GetRequiredService<ISyncedCollection<MissionStep>>(),
            sp.GetRequiredService<ISyncedLog<MissionEvent>>()));
        // Claude's tools for the missions (Helm's MCP server, Windows).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, MissionsMcpTools>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<MissionsViewModel>();
        return services;
    }
}
