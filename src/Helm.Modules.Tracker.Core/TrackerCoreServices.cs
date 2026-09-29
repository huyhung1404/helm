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
        services.AddSyncGroup("tracker.", TrackerIds.DisplayName);
        services.AddSingleton(sp => new TrackerStore(
            sp.GetRequiredService<ISyncedCollection<TrackerWorkspace>>(),
            sp.GetRequiredService<ISyncedCollection<TrackerItem>>(),
            sp.GetRequiredService<ISyncedLog<TrackerEvent>>()));
        services.AddSingleton(sp => new TrackerReminderService(
            sp.GetRequiredService<TrackerStore>(), sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>()));
        // Quick Capture: "/t buy milk tomorrow 9h" and "/d Nam 200k".
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget, TaskCaptureTarget>();
        services.AddSingleton<Helm.Core.Capture.ICaptureTarget, DebtCaptureTarget>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<TrackerViewModel>();
        return services;
    }

    /// <summary>
    /// Tasks and people in the debt book as link targets (notes link to them). The platform passes its Tracker content
    /// page, which opens a linked task or person.
    /// </summary>
    public static IServiceCollection AddTrackerLinks(this IServiceCollection services, Type contentPage)
    {
        void Show(IServiceProvider sp, Action<TrackerViewModel> show)
        {
            sp.GetRequiredService<IShellNavigation>().ShowPage(contentPage);
            show(sp.GetRequiredService<TrackerViewModel>());
        }
        return services.AddHelmLinks()
            .AddSingleton<ILinkProvider>(sp => new TaskLinkProvider(sp.GetRequiredService<TrackerStore>(), (ws, item) => Show(sp, vm => vm.ShowItem(ws, item))))
            .AddSingleton<ILinkProvider>(sp => new PersonLinkProvider(sp.GetRequiredService<TrackerStore>(), (book, key) => Show(sp, vm => vm.ShowPerson(book, key))));
    }
}
