using Helm.Core.Diagnostics;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Shell.BugReports;

public static class BugReportsServices
{
    /// <summary>The name of the page in the menu (next to What's new), the same on both apps.</summary>
    public const string PageTitle = "Bug reports";

    /// <summary>
    /// Bug reports on both apps: the black box, the two synced collections, the store, the AI agents' tools and the
    /// view models. The platform adds its input hooks, report window and pages.
    /// </summary>
    public static IServiceCollection AddBugReports(this IServiceCollection services, BugDeviceKind deviceKind)
    {
        services.AddSingleton(BlackBox.Shared);
        services.AddSyncedCollection(BugStore.ReportOptions);
        services.AddSyncedCollection(BugStore.EntryOptions);
        services.AddSyncGroup(BugStore.SyncPrefix, PageTitle);
        services.AddSingleton<IBugFiles>(sp => new BlobBugFiles(sp.GetRequiredService<BlobStore>()));
        services.AddSingleton(sp => new BugStore(
            sp.GetRequiredService<ISyncedCollection<BugReport>>(), sp.GetRequiredService<ISyncedCollection<BugEntry>>(),
            sp.GetRequiredService<IBugFiles>(), () => sp.GetRequiredService<IDeviceInfo>().DeviceName, deviceKind, AppInfo.Version));
        services.AddSingleton<IMcpToolProvider, BugMcpTools>(sp => new BugMcpTools(sp.GetRequiredService<BugStore>()));
        services.AddSingleton<BugHandOff>();
        services.AddSingleton<BugReportsViewModel>();
        return services;
    }

    /// <summary>
    /// What both apps record in the black box once services exist: sync's state changes and the context of a report
    /// (version, device, sync). Call once at start.
    /// </summary>
    public static void StartBlackBox(IServiceProvider services, Func<string?> currentPage)
    {
        var box = services.GetRequiredService<BlackBox>();
        var sync = services.GetRequiredService<SyncEngine>();
        var device = services.GetRequiredService<IDeviceInfo>();
        var last = sync.Status.State;
        sync.StatusChanged += (_, status) =>
        {
            if (status.State == last) return;
            last = status.State;
            if (status.State is SyncState.Syncing) return;
            box.Record(status.LastError is { } error && status.State is not SyncState.Idle ? BlackBoxKind.Error : BlackBoxKind.Sync,
                status.State switch
                {
                    SyncState.Idle => "Synced",
                    SyncState.Offline => "Sync: offline",
                    _ => $"Sync: {status.State}" + (status.LastError is { } e ? $" ({e})" : ""),
                }, "Sync");
        };
        box.AddContext("Helm", () => AppInfo.Version);
        box.AddContext("Device", () => $"{device.DeviceName} · {Environment.OSVersion.VersionString} · {(Environment.Is64BitProcess ? "64" : "32")}-bit");
        box.AddContext("Runtime", () => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        box.AddContext("Page", currentPage);
        box.AddContext("Sync", () => sync.Status.State + (sync.Status.LastSyncedAt is { } at ? $" · last {at.ToLocalTime():HH:mm:ss}" : ""));
        box.AddContext("Memory", () => $"{Environment.WorkingSet / (1024 * 1024)} MB working set, {GC.GetTotalMemory(false) / (1024 * 1024)} MB managed");
        box.AddContext("Culture", () => System.Globalization.CultureInfo.CurrentCulture.Name);
        // Kept in a field: an unreferenced timer would be collected.
        if (services.GetService<IUiDispatcher>() is { } ui) _stallWatch ??= new UiStallWatch(ui, box);
    }

    private static UiStallWatch? _stallWatch;
}
