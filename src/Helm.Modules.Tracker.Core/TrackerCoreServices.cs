using Helm.Core.Links;
using Helm.Core.Services;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Tracker;

public static class TrackerIds
{
    /// <summary>Module id on both apps: the settings file (tracker.json) and the enabled-state key.</summary>
    public const string ModuleId = "tracker";

    public const string DisplayName = "Tracker";

    public const string Description = "Keep to-do lists in workspaces, synced across your devices. Every completion is logged with its start and finish times for reports.";
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
        services.AddSyncGroup("tracker.", TrackerIds.DisplayName);
        services.AddSingleton(sp => new TrackerStore(
            sp.GetRequiredService<ISyncedCollection<TrackerWorkspace>>(),
            sp.GetRequiredService<ISyncedCollection<TrackerItem>>(),
            sp.GetRequiredService<ISyncedLog<TrackerEvent>>()));
        // The reminder also covers debts due in Wallet's debt book, and copies the Tracker's old debt book there.
        services.AddSingleton(sp => new TrackerReminderService(
            sp.GetRequiredService<TrackerStore>(), sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>(),
            debts: sp.GetService<Helm.Modules.Wallet.DebtBook>()));
        // Quick Capture: "/t buy milk tomorrow 9h".
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget, TaskCaptureTarget>();
        // Other tools hand tasks over (Missions sends a step here).
        services.AddSingleton<Helm.Core.Tasks.ITaskBridge, TrackerTaskBridge>();
        // Claude's tools for the to-do lists (Helm's MCP server, Windows).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, TrackerMcpTools>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<TrackerViewModel>();
        return services;
    }

    /// <summary>
    /// Tasks as link targets (notes link to them). The platform passes its Tracker content page, which opens a linked
    /// task.
    /// </summary>
    public static IServiceCollection AddTrackerLinks(this IServiceCollection services, Type contentPage)
    {
        void Show(IServiceProvider sp, Action<TrackerViewModel> show)
        {
            sp.GetRequiredService<IShellNavigation>().ShowPage(contentPage);
            show(sp.GetRequiredService<TrackerViewModel>());
        }
        return services.AddHelmLinks()
            .AddSingleton<ILinkProvider>(sp => new TaskLinkProvider(sp.GetRequiredService<TrackerStore>(), (ws, item) => Show(sp, vm => vm.ShowItem(ws, item))));
    }
}
