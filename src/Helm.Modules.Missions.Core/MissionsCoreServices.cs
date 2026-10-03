using Helm.Core.Links;
using Helm.Core.Services;
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
        services.AddSingleton(sp => new MissionReminderService(
            sp.GetRequiredService<MissionsStore>(), sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>()));
        // A step sent to Tracker (or another to-do list) and its task finish together. Resolved lazily: Tracker may
        // register after Missions, or not at all.
        services.AddSingleton(sp => new MissionTaskSync(sp.GetRequiredService<MissionsStore>(), sp.GetServices<Helm.Core.Tasks.ITaskBridge>,
            sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<MissionTaskSync>>()));
        // Quick Capture and Android's Share → "Save to Helm": an AI's answer with a mission in it.
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget, MissionCaptureTarget>();
        // Claude's tools for the missions (Helm's MCP server, Windows).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, MissionsMcpTools>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<MissionsViewModel>();
        return services;
    }

    /// <summary>
    /// Missions as link targets (notes link to them). The platform passes its Missions content page, which shows a
    /// linked mission.
    /// </summary>
    public static IServiceCollection AddMissionLinks(this IServiceCollection services, Type contentPage) =>
        services.AddHelmLinks()
            .AddSingleton<ILinkProvider>(sp => new MissionLinkProvider(sp.GetRequiredService<MissionsStore>(), id =>
            {
                sp.GetRequiredService<IShellNavigation>().ShowPage(contentPage);
                sp.GetRequiredService<MissionsViewModel>().Select(id);
            }));
}
