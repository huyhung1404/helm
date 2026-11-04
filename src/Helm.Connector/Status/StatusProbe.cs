using Helm.Connector.Hosting;
using Helm.Connector.Sync;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Connector.Status;

/// <summary>
/// Looks at this box's connector without changing it: the data folder, the sync files, what it syncs, the holds in the
/// replica, and what a running serve says of itself (its sync state and push breaker). Nothing is created: the replica
/// is opened only once sync is set up here.
/// </summary>
internal sealed class StatusProbe(HelmPaths paths, ISecretProtector secrets, string hostKey, SyncSelection selection, string device)
{
    public StatusReport Probe()
    {
        var credentials = new ProtectedSyncCredentialStore(paths.SyncCredentialsFile, secrets).Load();
        var unlocked = new ProtectedMasterKeyStore(paths.SyncKeyFile, secrets).Load() is not null;
        string? problem = null;
        if (credentials is null && File.Exists(paths.SyncCredentialsFile)) problem = "the sync token cannot be read with this box's host key";
        else if (!unlocked && File.Exists(paths.SyncKeyFile)) problem = "the account key cannot be read with this box's host key";

        // The replica only once the token opened with this key: a key that does not fit would make a new, empty one.
        string? deviceId = null;
        IReadOnlyList<HoldReport> holds = [];
        if (credentials is not null && File.Exists(paths.SyncDatabaseFile))
        {
            try
            {
                using var services = ConnectorHost.Build(paths, secrets, NullLoggerFactory.Instance, selection);
                var engine = services.GetRequiredService<SyncEngine>();
                deviceId = engine.DeviceId;
                holds = [.. engine.Holds.Select(h => new HoldReport(h.Collection, h.Records.Count, h.LiveRecords))];
            }
            catch (Exception ex)
            {
                problem ??= "the sync replica cannot be read: " + ex.Message;
            }
        }

        return new StatusReport
        {
            Version = AppInfo.Version,
            Device = device,
            DataDirectory = paths.Root,
            DataDirectoryExists = Directory.Exists(paths.Root),
            HostKey = hostKey,
            Sync = new SyncReport(credentials is not null, unlocked, credentials?.Server.ToString(), deviceId, problem),
            Groups = [.. selection.Groups.Select(g => new GroupReport(g, SyncSelection.Known[g]))],
            Serve = ServeState.Read(paths) is { } state
                ? new ServeReport(state.Pid, state.Started, state.SyncState, state.LastSyncedAt, state.LastError, state.Live, state.Breaker,
                    state.BreakerAt, PushBreaker.MaxPushesPerMinute, state.McpSocket)
                : null,
            Holds = holds,
        };
    }
}
