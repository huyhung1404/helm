using Helm.Connector.Hosting;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Sync;

/// <summary>
/// Reports the deletions sync holds back (another device deleted many records of a guarded collection, see
/// <see cref="SyncEngine.Holds"/>) and never decides them: that is the user's, on the phone or the PC. Each new hold is
/// logged and pinged to the health check once; the rest of sync goes on meanwhile (a hold is per collection).
/// </summary>
internal sealed class HoldWatcher(SyncEngine engine, HealthPing health, ILogger logger) : IServePart
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Start()
    {
        engine.StatusChanged += OnStatus;
        Check();
    }

    public void Dispose() => engine.StatusChanged -= OnStatus;

    /// <summary>Reports the holds not reported yet; a hold that went and came back is new again.</summary>
    internal void Check()
    {
        var holds = engine.Holds;
        List<SyncHold> fresh;
        lock (_gate)
        {
            _reported.IntersectWith(holds.Select(h => h.Collection));
            fresh = [.. holds.Where(h => _reported.Add(h.Collection))];
        }
        foreach (var hold in fresh)
        {
            var reason = $"Sync holds {hold.Records.Count} deletions of {hold.Collection} made on another device ({hold.LiveRecords} records here). "
                + "The connector never decides: approve or keep them on the phone or the PC.";
            logger.LogWarning("{Reason}", reason);
            _ = health.FailAsync(reason);
        }
    }

    private void OnStatus(object? sender, SyncStatus status) => Check();
}
