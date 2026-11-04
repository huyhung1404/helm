using Helm.Ai.Agents;
using Helm.Ai.Assistant;
using Helm.Connector.Hosting;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Connector.Status;

/// <summary>
/// Looks at this box's Helm without changing it: the data folder, the sync files, the agents' CLIs and sign-ins, and
/// the workers the replica last heard of. Nothing is created (the replica is opened only once sync is set up here).
/// </summary>
/// <param name="findCli">Where an agent kind's CLI is installed (<see cref="AgentCommands.Of"/>), or null.</param>
internal sealed class StatusProbe(HelmPaths paths, ISecretProtector secrets, string hostKey, Func<string, string?> findCli, string device,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public StatusReport Probe()
    {
        var credentials = new ProtectedSyncCredentialStore(paths.SyncCredentialsFile, secrets).Load();
        var unlocked = new ProtectedMasterKeyStore(paths.SyncKeyFile, secrets).Load() is not null;
        string? problem = null;
        if (credentials is null && File.Exists(paths.SyncCredentialsFile)) problem = "the sync token cannot be read with this box's host key";
        else if (!unlocked && File.Exists(paths.SyncKeyFile)) problem = "the account key cannot be read with this box's host key";

        // The replica only once the token opened with this key: a key that does not fit would make a new, empty one.
        string? deviceId = null;
        IReadOnlyList<WorkerReport> workers = [];
        if (credentials is not null && File.Exists(paths.SyncDatabaseFile))
        {
            try
            {
                (deviceId, workers) = ReadReplica();
            }
            catch (Exception ex)
            {
                problem ??= "the sync replica cannot be read: " + ex.Message;
            }
        }

        var clis = AgentKinds.All.Select(k => new CliReport(k.Id, k.Name, findCli(k.Id))).ToList();
        return new StatusReport
        {
            Version = AppInfo.Version,
            Device = device,
            DataDirectory = paths.Root,
            DataDirectoryExists = Directory.Exists(paths.Root),
            HostKey = hostKey,
            Sync = new SyncReport(credentials is not null, unlocked, credentials?.Server.ToString(), deviceId, problem),
            Clis = clis,
            Agents = Agents(clis),
            Workers = workers,
        };
    }

    private IReadOnlyList<AgentReport> Agents(IReadOnlyList<CliReport> clis)
    {
        using var store = new SettingsStore<AiAgentSettings>(paths.SettingsFile(AiAgentSettings.StoreId));
        return [.. store.Current.Agents.Select(p =>
        {
            var signedIn = AgentRegistry.IsSignedIn(paths, p);
            var installed = clis.FirstOrDefault(c => c.Kind == p.Kind)?.Path is not null;
            var state = !p.Enabled ? "off" : !installed ? "not installed" : !signedIn ? "not signed in" : "ready";
            return new AgentReport(p.Name, p.Kind, p.Enabled, signedIn, installed, state);
        })];
    }

    private (string DeviceId, IReadOnlyList<WorkerReport> Workers) ReadReplica()
    {
        using var services = ConnectorHost.Build(paths, secrets, NullLoggerFactory.Instance);
        var self = services.GetRequiredService<SyncEngine>().DeviceId;
        var now = _time.GetUtcNow();
        var workers = services.GetRequiredService<ISyncedCollection<AiWorkerRecord>>().All()
            .Select(w => new WorkerReport(w.Value.Device, w.Id == self, now - w.Value.SeenAt < AgentJobs.Online, w.Value.SeenAt, w.Value.Agents, w.Value.Problem))
            .OrderByDescending(w => w.ThisDevice).ThenByDescending(w => w.SeenAt)
            .ToList();
        return (self, workers);
    }
}
