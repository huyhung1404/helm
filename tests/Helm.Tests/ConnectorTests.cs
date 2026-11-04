using System.Text.Json;
using Helm.Ai.Agents;
using Helm.Ai.Assistant;
using Helm.Connector;
using Helm.Connector.Hosting;
using Helm.Connector.Status;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>helm, the headless Helm for a Linux box: its command line, <c>helm status</c> and what <c>helm serve</c> hosts.</summary>
public sealed class ConnectorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new();

    private string Data => Path.Combine(_dir.Path, "helm");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task PrintsItsVersion()
    {
        var (exit, output, _) = await RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Equal($"helm {AppInfo.Version}", output.Trim());
    }

    [Fact]
    public async Task ACommandIsNeeded()
    {
        var (none, help, _) = await RunAsync();
        var (unknown, _, error) = await RunAsync("deploy");

        Assert.Equal(ConnectorApp.Usage, none);
        Assert.Contains("status", help, StringComparison.Ordinal);
        Assert.Equal(ConnectorApp.Usage, unknown);
        Assert.Contains("'deploy' is not a command", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDataFolderComesFromTheOptionTheVariableThenXdg()
    {
        var env = new Dictionary<string, string> { ["HOME"] = "/home/hung", ["XDG_DATA_HOME"] = "/data", ["HELM_DATA_DIR"] = "/srv/fairy/helm" };
        string? Env(string name) => env.GetValueOrDefault(name);

        Assert.Equal(Path.GetFullPath("/opt/helm"), DataDirectory.Resolve("/opt/helm", Env));
        Assert.Equal(Path.GetFullPath("/srv/fairy/helm"), DataDirectory.Resolve(null, Env));
        env.Remove("HELM_DATA_DIR");
        Assert.Equal(Path.Combine("/data", "helm"), DataDirectory.Resolve(null, Env));
        env["XDG_DATA_HOME"] = "relative";
        Assert.Equal(Path.Combine("/home/hung", ".local", "share", "helm"), DataDirectory.Resolve(null, Env));
    }

    [Fact]
    public async Task StatusOfANewBoxChangesNothing()
    {
        var (exit, output, _) = await RunAsync("--data-dir", Data, "status");

        Assert.Equal(0, exit);
        Assert.Contains("(not created yet)", output, StringComparison.Ordinal);
        Assert.Contains("Sync       not connected", output, StringComparison.Ordinal);
        Assert.Contains("none yet", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task StatusShowsSyncTheAgentsAndTheWorkers()
    {
        var paths = new HelmPaths(Data);
        var secrets = ConnectorSecrets.For(paths, _ => null);
        new ProtectedSyncCredentialStore(paths.SyncCredentialsFile, secrets).Save(new SyncCredentials(new Uri("https://sync.invalid/"), Token()));
        new ProtectedMasterKeyStore(paths.SyncKeyFile, secrets).Save(new byte[32]);
        string self;
        await using (var services = ConnectorHost.Build(paths, secrets, NullLoggerFactory.Instance))
        {
            self = services.GetRequiredService<SyncEngine>().DeviceId;
            var workers = services.GetRequiredService<ISyncedCollection<AiWorkerRecord>>();
            workers.Upsert("pc", new AiWorkerRecord { Device = "HUNG-PC", Agents = ["Codex"], SeenAt = Now.AddMinutes(-10) });
            workers.Upsert(self, new AiWorkerRecord { Device = "fairy", Agents = ["Claude Code"], SeenAt = Now.AddSeconds(-20) });
        }
        var claude = new AgentProfile { Kind = AgentKinds.ClaudeCode, Name = "Claude Code" };
        using (var store = new SettingsStore<AiAgentSettings>(paths.SettingsFile(AiAgentSettings.StoreId)))
            store.Update(s => s.Agents.AddRange([claude, new AgentProfile { Kind = AgentKinds.Codex, Name = "Codex" },
                new AgentProfile { Kind = AgentKinds.Grok, Name = "Grok" }, new AgentProfile { Kind = AgentKinds.Grok, Name = "Grok off", Enabled = false }]));
        Directory.CreateDirectory(AgentRegistry.HomeOf(paths, claude.Id));
        File.WriteAllText(Path.Combine(AgentRegistry.HomeOf(paths, claude.Id), ".credentials.json"), "{}");

        var report = new StatusProbe(paths, secrets, secrets.KeySource, kind => kind == AgentKinds.Grok ? null : "/usr/local/bin/" + kind, "fairy",
            new FixedTime(Now)).Probe();
        var output = new StringWriter();
        StatusPrinter.Write(report, output, Now);
        var text = output.ToString();

        Assert.True(report.Sync is { Connected: true, Unlocked: true, Problem: null });
        Assert.Equal(self, report.Sync.DeviceId);
        Assert.Equal(["ready", "not signed in", "not installed", "off"], report.Agents.Select(a => a.State));
        Assert.Contains("connected to https://sync.invalid/, unlocked", text, StringComparison.Ordinal);
        Assert.Contains("Grok         not found", text, StringComparison.Ordinal);
        Assert.Contains("fairy (this box)         online, seen 20 s ago: Claude Code", text, StringComparison.Ordinal);
        Assert.Contains("HUNG-PC                  offline, seen 10 min ago: Codex", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("fairy (this box)", StringComparison.Ordinal) < text.IndexOf("HUNG-PC", StringComparison.Ordinal));

        var (exit, json, _) = await RunAsync("--data-dir", Data, "status", "--json");
        Assert.Equal(0, exit);
        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("sync").GetProperty("unlocked").GetBoolean());
        Assert.Equal(4, parsed.RootElement.GetProperty("agents").GetArrayLength());
    }

    [Fact]
    public void StatusSaysWhenTheHostKeyDoesNotFitAndLeavesTheReplicaAlone()
    {
        var paths = new HelmPaths(Data);
        // Written with another key (here: none at all), as after copying the data folder from another box.
        new ProtectedSyncCredentialStore(paths.SyncCredentialsFile, new PlainSecretProtector())
            .Save(new SyncCredentials(new Uri("https://sync.invalid/"), Token()));
        var secrets = ConnectorSecrets.For(paths, _ => null);

        var report = new StatusProbe(paths, secrets, secrets.KeySource, _ => null, "fairy").Probe();

        Assert.False(report.Sync.Connected);
        Assert.Equal("the sync token cannot be read with this box's host key", report.Sync.Problem);
        Assert.False(File.Exists(paths.SyncDatabaseFile));
        Assert.False(Directory.Exists(ConnectorSecrets.Directory(paths)));
    }

    [Fact]
    public async Task ServeNeedsSyncSetUp()
    {
        var (exit, _, error) = await RunAsync("--data-dir", Data, "serve");

        Assert.Equal(ConnectorApp.Failed, exit);
        Assert.Contains("Helm Sync is not set up on this box", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServeHostsTheAgentsAndTheToolsStores()
    {
        var paths = new HelmPaths(Data);
        await using var services = ConnectorHost.Build(paths, new PlainSecretProtector(), NullLoggerFactory.Instance, s =>
        {
            s.AddAgentProxy(paths, _ => new TerminalAgentSignIn(TextWriter.Null));
            HeadlessTools.Add(s);
        });

        var tools = services.GetServices<IMcpToolProvider>().SelectMany(p => p.Tools).Select(t => t.Name).ToHashSet();
        Assert.Contains(services.GetServices<IAgentRoute>(), r => r is AgentRegistry);
        Assert.NotNull(services.GetRequiredService<AgentJobWorker>());
        Assert.NotNull(services.GetRequiredService<Helm.Ai.Vps.VpsWorkerControl>());
        Assert.IsType<PlainChildProcessLauncher>(services.GetRequiredService<IChildProcessLauncher>());
        foreach (var tool in new[] { "tracker_add_task", "wallet_add_debt", "habit_check", "notes_create", "mission_create", "vocab_add_words", "ai_briefs" })
            Assert.Contains(tool, tools);
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await ConnectorApp.RunAsync(args, new CliConsole(output, error, _ => null));
        return (exit, output.ToString(), error.ToString());
    }

    private static string Token()
    {
        var body = $"{SyncToken.Prefix}{new string('a', 16)}_{new string('b', 40)}";
        return body + SyncToken.Checksum(body);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
