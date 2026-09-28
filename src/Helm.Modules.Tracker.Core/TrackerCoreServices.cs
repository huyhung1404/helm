using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Tracker;

public static class TrackerIds
{
    /// <summary>Module id on both apps: the settings file (tracker.json) and the enabled-state key.</summary>
    public const string ModuleId = "tracker";

    public const string DisplayName = "Tracker";

    public const string Description = "Keep to-do lists, debt books and other lists in workspaces, synced across your devices. Every completion is logged with its start and finish times for reports.";
}

public static class TrackerCoreServices
{
    /// <summary>
    /// The platform-neutral part of Tracker: its three synced collections, the store and the page view model. The
    /// platform module (Windows or Android) calls this from its own <c>AddTrackerModule()</c>.
    /// </summary>
    public static IServiceCollection AddTrackerCore(this IServiceCollection services)
    {
        // Items and workspaces are small records edited as a whole: the latest edit wins. History is append-only.
        services.AddSyncedCollection(new SyncedCollectionOptions<TrackerWorkspace> { Name = TrackerStore.WorkspacesCollection });
        services.AddSyncedCollection(new SyncedCollectionOptions<TrackerItem> { Name = TrackerStore.ItemsCollection });
        services.AddSyncedLog<TrackerEvent>(TrackerStore.HistoryCollection);
        services.AddSingleton(sp => new TrackerStore(
            sp.GetRequiredService<ISyncedCollection<TrackerWorkspace>>(),
            sp.GetRequiredService<ISyncedCollection<TrackerItem>>(),
            sp.GetRequiredService<ISyncedLog<TrackerEvent>>()));
        services.AddSingleton(sp => new TrackerReminderService(
            sp.GetRequiredService<TrackerStore>(), sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>()));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<TrackerViewModel>();
        return services;
    }
}
