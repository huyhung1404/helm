using Helm.Core.Sync;
using Helm.Core.Today;

namespace Helm.Shell.Home;

/// <summary>Home's alert when sync needs the user: the device was removed, the key changed, storage is full, deletions wait.</summary>
public sealed class SyncAlertSource : IHomeAlertSource
{
    private readonly SyncEngine _engine;
    private readonly IHomeLinks _links;

    public SyncAlertSource(SyncEngine engine, IHomeLinks links)
    {
        _engine = engine;
        _links = links;
        engine.StatusChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<HomeAlertData> Current()
    {
        var alerts = new List<HomeAlertData>();
        switch (_engine.Status.State)
        {
            case SyncState.Unauthorized:
                alerts.Add(new("sync", "Sync stopped: this device was removed", "Its token was revoked or expired. Connect again in General.", "Open Sync", 30));
                break;
            case SyncState.KeyChanged:
                alerts.Add(new("sync", "Enter your passphrase to keep syncing", "The account key was changed on another device. Nothing on this device was lost.", "Unlock", 30));
                break;
            case SyncState.QuotaExceeded:
                alerts.Add(new("sync", "Sync storage is full", "Edits stay on this device but are not uploaded.", "Open Sync", 30));
                break;
        }
        if (_engine.Holds.Count > 0)
            alerts.Add(new("sync-hold", "Deletions from another device are waiting", "Choose whether to delete them here too.", "Review", 31));
        return alerts;
    }

    public Task Act(string alertId)
    {
        _links.OpenGeneral("sync");
        return Task.CompletedTask;
    }
}
