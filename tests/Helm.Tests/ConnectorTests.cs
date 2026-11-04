using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Connector;
using Helm.Connector.Hosting;
using Helm.Connector.Mcp;
using Helm.Connector.Status;
using Helm.Connector.Sync;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// helm-connector, Helm's data on the user's Linux home server: its command line, what it syncs, <c>status</c>, the
/// push breaker, the hold reports, and Helm's tools over MCP for Fairy Core.
/// </summary>
public sealed class ConnectorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new();
    private readonly List<IDisposable> _owned = [];

    private string Data => Path.Combine(_dir.Path, "helm");

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task PrintsItsVersion()
    {
        var (exit, output, _) = await RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Equal($"helm-connector {AppInfo.Version}", output.Trim());
    }

    [Fact]
    public async Task ACommandIsNeeded()
    {
        var (none, help, _) = await RunAsync();
        var (unknown, _, error) = await RunAsync("deploy");

        Assert.Equal(ConnectorApp.Usage, none);
        Assert.Contains("status", help, StringComparison.Ordinal);
        Assert.Contains("mcp", help, StringComparison.Ordinal);
        Assert.Equal(ConnectorApp.Usage, unknown);
        Assert.Contains("'deploy' is not a command", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDataFolderComesFromTheOptionTheVariableThenXdg()
    {
        var env = new Dictionary<string, string> { ["HOME"] = "/home/hung", ["XDG_DATA_HOME"] = "/data", ["HELM_DATA_DIR"] = "/srv/fairy/connectors/helm" };
        string? Env(string name) => env.GetValueOrDefault(name);

        Assert.Equal(Path.GetFullPath("/opt/helm"), DataDirectory.Resolve("/opt/helm", Env));
        Assert.Equal(Path.GetFullPath("/srv/fairy/connectors/helm"), DataDirectory.Resolve(null, Env));
        env.Remove("HELM_DATA_DIR");
        Assert.Equal(Path.Combine("/data", "helm"), DataDirectory.Resolve(null, Env));
        env["XDG_DATA_HOME"] = "relative";
        Assert.Equal(Path.Combine("/home/hung", ".local", "share", "helm"), DataDirectory.Resolve(null, Env));
    }

    [Fact]
    public void WhatItSyncsComesFromTheOptionTheFileThenTheDefault()
    {
        Directory.CreateDirectory(Data);
        Assert.Equal(["ai.", "wallet.", "tracker.", "habits."], SyncSelection.Resolve(null, Data).Groups);
        Assert.Equal(["ai.", "wallet.", "tracker.", "habits.", "settings.ai-features"], SyncSelection.Resolve(null, Data).Prefixes);

        File.WriteAllText(Path.Combine(Data, SyncSelection.FileName), """{ "sync": ["wallet", "Notes."] }""");
        Assert.Equal(["wallet.", "notes."], SyncSelection.Resolve(null, Data).Prefixes);
        Assert.Equal(["tracker.", "missions."], SyncSelection.Resolve(" tracker , missions", Data).Groups);

        Assert.Throws<FormatException>(() => SyncSelection.Resolve("vault", Data));
        Assert.Throws<FormatException>(() => SyncSelection.Resolve("", Data));
        File.WriteAllText(Path.Combine(Data, SyncSelection.FileName), "{ sync: ");
        Assert.Throws<FormatException>(() => SyncSelection.Resolve(null, Data));
    }

    [Fact]
    public async Task AnUnknownToolToSyncIsAUsageError()
    {
        var (exit, _, error) = await RunAsync("--data-dir", Data, "--sync", "ai,vault", "status");

        Assert.Equal(ConnectorApp.Usage, exit);
        Assert.Contains("'vault' is not something the connector syncs", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusOfANewBoxChangesNothing()
    {
        var (exit, output, _) = await RunAsync("--data-dir", Data, "status");

        Assert.Equal(0, exit);
        Assert.Contains("(not created yet)", output, StringComparison.Ordinal);
        Assert.Contains("Sync       not connected (helm-connector sync connect)", output, StringComparison.Ordinal);
        Assert.Contains("Syncs      AI (ai.), Wallet (wallet.), Tracker (tracker.), Habits (habits.)", output, StringComparison.Ordinal);
        Assert.Contains("Serve      not running", output, StringComparison.Ordinal);
        Assert.Contains("Holds      none", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task StatusShowsSyncAndWhatTheRunningServeSays()
    {
        var paths = new HelmPaths(Data);
        var secrets = ConnectorSecrets.For(paths, _ => null);
        new ProtectedSyncCredentialStore(paths.SyncCredentialsFile, secrets).Save(new SyncCredentials(new Uri("https://sync.invalid/"), Token()));
        new ProtectedMasterKeyStore(paths.SyncKeyFile, secrets).Save(new byte[32]);
        string self;
        await using (var services = ConnectorHost.Build(paths, secrets, NullLoggerFactory.Instance, SyncSelection.Resolve(null, Data)))
            self = services.GetRequiredService<SyncEngine>().DeviceId;
        // This test's own process plays the running serve.
        new ServeState
        {
            Pid = Environment.ProcessId, Started = Now.AddHours(-1), SyncState = "Error", LastSyncedAt = Now.AddMinutes(-3), Live = false,
            Breaker = "more than 20 pushes within a minute", BreakerAt = Now.AddMinutes(-2), McpSocket = "/run/helm-connector/mcp.sock",
        }.Write(paths);

        var report = new StatusProbe(paths, secrets, secrets.KeySource, SyncSelection.Resolve("wallet,notes", Data), "fairy").Probe();
        var output = new StringWriter();
        StatusPrinter.Write(report, output, Now);
        var text = output.ToString();

        Assert.True(report.Sync is { Connected: true, Unlocked: true, Problem: null });
        Assert.Equal(self, report.Sync.DeviceId);
        Assert.Contains("connected to https://sync.invalid/, unlocked", text, StringComparison.Ordinal);
        Assert.Contains("Syncs      Wallet (wallet.), Notes (notes.)", text, StringComparison.Ordinal);
        Assert.Contains($"(pid {Environment.ProcessId}): Error, last sync 3 min ago", text, StringComparison.Ordinal);
        Assert.Contains("Throttle   TRIPPED 2 min ago: more than 20 pushes within a minute", text, StringComparison.Ordinal);
        Assert.Contains("MCP        /run/helm-connector/mcp.sock", text, StringComparison.Ordinal);

        var (exit, json, _) = await RunAsync("--data-dir", Data, "status", "--json");
        Assert.Equal(0, exit);
        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("sync").GetProperty("unlocked").GetBoolean());
        Assert.Equal("more than 20 pushes within a minute", parsed.RootElement.GetProperty("serve").GetProperty("breaker").GetString());
        ServeState.Remove(paths);
        Assert.Null(ServeState.Read(paths));
    }

    [Fact]
    public void StatusListsTheHolds()
    {
        var report = new StatusReport
        {
            Version = "1", Device = "fairy", DataDirectory = "/srv", HostKey = "k", Sync = new SyncReport(true, true, "https://s/", "d", null),
            Holds = [new HoldReport("wallet.transactions", 42, 300)],
        };
        var output = new StringWriter();
        StatusPrinter.Write(report, output, Now);

        Assert.Contains("wallet.transactions      42 deletions from another device wait (300 records here)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("approve or keep them on the phone", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSaysWhenTheHostKeyDoesNotFitAndLeavesTheReplicaAlone()
    {
        var paths = new HelmPaths(Data);
        // Written with another key (here: none at all), as after copying the data folder from another box.
        new ProtectedSyncCredentialStore(paths.SyncCredentialsFile, new PlainSecretProtector())
            .Save(new SyncCredentials(new Uri("https://sync.invalid/"), Token()));
        var secrets = ConnectorSecrets.For(paths, _ => null);

        var report = new StatusProbe(paths, secrets, secrets.KeySource, SyncSelection.Resolve(null, Data), "fairy").Probe();

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
    public async Task SyncSetupChecksWhatItIsGiven()
    {
        var (usage, _, help) = await RunAsync("--data-dir", Data, "sync", "connect", "--token");
        var (unlock, _, notConnected) = await RunAsync("--data-dir", Data, "sync", "unlock");
        var (bad, _, badToken) = await RunAsync("--data-dir", Data, "sync", "connect", "--token", "helm_pat_typo");

        Assert.Equal(ConnectorApp.Usage, usage);
        Assert.Contains("--invite CODE --account NAME", help, StringComparison.Ordinal);
        Assert.Equal(ConnectorApp.Failed, unlock);
        Assert.Contains("not connected yet", notConnected, StringComparison.Ordinal);
        Assert.Equal(ConnectorApp.Failed, bad);
        Assert.Contains("not a Helm sync token", badToken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBreakerStopsSyncAfterTooManyPushesInAMinute()
    {
        var server = new FakeSyncServer();
        var time = new ManualTime(Now);
        var breaker = new PushBreaker(server.Connect(), time);
        var trips = new List<string>();
        breaker.Tripped += trips.Add;
        var engine = Own(new SyncEngine(Own(new SyncDatabase(Path.Combine(_dir.Path, "box.db"), TestKeys.Local)), breaker,
            new InMemoryMasterKeyStore(SyncKeyring.CreateMasterKey()), [], time: time, debounce: TimeSpan.FromHours(1)));
        var items = Own(new SyncedCollection<JsonObject>(engine, new() { Name = "wallet.items" }));

        // About one a minute, as the box normally does, for half an hour: never trips.
        for (var i = 0; i < 30; i++)
        {
            items.Upsert($"slow{i}", new JsonObject { ["n"] = i });
            Assert.Equal(SyncRunOutcome.Completed, (await engine.SyncNowAsync()).Outcome);
            time.Advance(TimeSpan.FromSeconds(55));
        }
        time.Advance(TimeSpan.FromMinutes(2));
        // A loop: twenty pushes within the minute go, the next one trips it.
        for (var i = 0; i < PushBreaker.MaxPushesPerMinute; i++)
        {
            items.Upsert($"fast{i}", new JsonObject { ["n"] = i });
            Assert.Equal(SyncRunOutcome.Completed, (await engine.SyncNowAsync()).Outcome);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        items.Upsert("one-too-many", new JsonObject());
        Assert.Equal(SyncRunOutcome.Failed, (await engine.SyncNowAsync()).Outcome);

        var reason = Assert.Single(trips);
        Assert.Contains("more than 20 pushes within a minute", reason, StringComparison.Ordinal);
        Assert.Equal(reason, breaker.TrippedReason);
        Assert.False(breaker.IsConfigured);
        // Stopped for good, not for a minute: nothing more reaches the server until a restart.
        time.Advance(TimeSpan.FromHours(1));
        var pushed = server.Records.Count;
        items.Upsert("later", new JsonObject());
        Assert.Equal(SyncRunOutcome.NotConfigured, (await engine.SyncNowAsync()).Outcome);
        Assert.Equal(pushed, server.Records.Count);
        await Assert.ThrowsAsync<SyncPushBreakerException>(() => breaker.PullAsync(0, 10, SyncPullFilter.None, default));
        Assert.Single(trips);
    }

    [Fact]
    public async Task TheHealthCheckHearsOfFailuresOnly()
    {
        var handler = new RecordingHandler();
        await new HealthPing("https://hc-ping.com/abc/", new HttpClient(handler), NullLogger.Instance).FailAsync("sync stopped");
        await new HealthPing(null, new HttpClient(handler), NullLogger.Instance).FailAsync("nothing");

        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://hc-ping.com/abc/fail", url);
        Assert.Equal("sync stopped", body);
        Assert.False(HealthPing.From(_ => null, NullLogger.Instance).Enabled);
    }

    [Fact]
    public async Task EachNewHoldIsReportedOnceAndNeverDecided()
    {
        var server = new FakeSyncServer();
        var key = SyncKeyring.CreateMasterKey();
        (SyncEngine Engine, SyncedCollection<JsonObject> Items) Device(string name)
        {
            var engine = Own(new SyncEngine(Own(new SyncDatabase(Path.Combine(_dir.Path, name + ".db"), TestKeys.Local)), server.Connect(),
                new InMemoryMasterKeyStore(key), [], debounce: TimeSpan.FromHours(1)));
            return (engine, Own(new SyncedCollection<JsonObject>(engine, new() { Name = "wallet.items", GuardDeletions = true })));
        }
        var phone = Device("phone");
        var box = Device("box");
        for (var i = 0; i < 20; i++) phone.Items.Upsert($"t{i}", new JsonObject { ["n"] = i });
        await phone.Engine.SyncNowAsync();
        await box.Engine.SyncNowAsync();
        var handler = new RecordingHandler();
        using var watcher = new HoldWatcher(box.Engine, new HealthPing("https://hc.invalid/x", new HttpClient(handler), NullLogger.Instance),
            NullLogger.Instance);
        watcher.Start();

        for (var i = 0; i < 8; i++) phone.Items.Delete($"t{i}");
        await phone.Engine.SyncNowAsync();
        Assert.Equal(SyncRunOutcome.Held, (await box.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(SyncRunOutcome.Held, (await box.Engine.SyncNowAsync()).Outcome);
        await handler.Settled();

        var (url, body) = Assert.Single(handler.Requests);
        Assert.EndsWith("/x/fail", url, StringComparison.Ordinal);
        Assert.Contains("8 deletions of wallet.items", body, StringComparison.Ordinal);
        // Nothing was applied: the records stay until the user decides.
        Assert.Equal(20, box.Items.All().Count);
    }

    [Fact]
    public async Task ByDefaultItServesWalletTrackerHabitsAndAiButNeverVault()
    {
        await using var services = Tools(SyncSelection.Resolve(null, Data));
        var tools = ConnectorMcp.Tools(services.GetServices<IMcpToolProvider>()).Select(t => t.Name).ToHashSet();

        foreach (var tool in new[] { "tracker_add_task", "wallet_add_debt", "wallet_summary", "habit_check", "ai_briefs" }) Assert.Contains(tool, tools);
        Assert.DoesNotContain(tools, t => t.StartsWith("notes_", StringComparison.Ordinal) || t.StartsWith("mission", StringComparison.Ordinal)
            || t.StartsWith("vocab_", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, t => t.Contains("vault", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(services.GetServices<IMcpToolProvider>(), p => p.GetType().Namespace?.Contains("Vault", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(services.GetServices<SyncGroup>(), g => g.Prefix == "vault.");
    }

    [Fact]
    public async Task DeletingOrReplacingIsRefusedAndTheRestRunsWithoutAQuestion()
    {
        await using var services = Tools(SyncSelection.Resolve("ai,wallet,tracker,habits,notes,missions,vocab", Data));
        var server = ConnectorMcp.Server(services.GetServices<IMcpToolProvider>(), NullLogger.Instance);
        Assert.Contains("vocab_add_words", server.ToolNames);

        var created = await CallAsync(server, "notes_create", new JsonObject { ["title"] = "Box", ["text"] = "from Fairy Core" });
        Assert.False(created["isError"]!.GetValue<bool>());
        var id = JsonNode.Parse(created["content"]![0]!["text"]!.GetValue<string>())!["id"]!.GetValue<string>();

        var trashed = await CallAsync(server, "notes_trash", new JsonObject { ["id"] = id });
        Assert.True(trashed["isError"]!.GetValue<bool>());
        Assert.Contains("on their phone", trashed["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(services.GetRequiredService<Helm.Modules.Notes.NotesStore>().Trash());
        Assert.Equal(["mission_replan", "notes_trash"],
            services.GetServices<IMcpToolProvider>().SelectMany(p => p.Tools).Where(t => t.Destructive).Select(t => t.Name).Order());
    }

    [Fact]
    public async Task McpRelaysToTheRunningServesSocket()
    {
        var socket = Path.Combine(_dir.Path, "mcp.sock");
        var (none, _, error) = await RunAsync(["--data-dir", Data, "mcp"], new() { ["HELM_MCP_SOCKET"] = socket });
        Assert.Equal(ConnectorApp.Failed, none);
        Assert.Contains("serve is not running", error, StringComparison.Ordinal);
        var tooLong = Path.Combine(_dir.Path, new string('s', 120));
        var (longPath, _, longError) = await RunAsync(["--data-dir", Data, "mcp"], new() { ["HELM_MCP_SOCKET"] = tooLong });
        Assert.Equal(ConnectorApp.Failed, longPath);
        Assert.Contains("too long", longError, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new McpSocketHost(tooLong, () => throw new InvalidOperationException(), NullLogger.Instance).Start());

        await using var services = Tools(SyncSelection.Resolve(null, Data));
        using var host = new McpSocketHost(socket, () => ConnectorMcp.Server(services.GetServices<IMcpToolProvider>(), NullLogger.Instance),
            NullLogger.Instance);
        host.Start();
        Assert.Throws<InvalidOperationException>(() => new McpSocketHost(socket, () => throw new InvalidOperationException(), NullLogger.Instance).Start());

        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"fairy-core"}}}""" + "\n"
            + """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\n"));
        var output = new MemoryStream();
        var (exit, _, _) = await RunAsync(["--data-dir", Data, "mcp"], new() { ["HELM_MCP_SOCKET"] = socket }, input, output);

        Assert.Equal(0, exit);
        var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, replies.Count);
        Assert.Equal("helm", replies[0]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains(replies[1]["result"]!["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "wallet_add_transaction");
    }

    private ServiceProvider Tools(SyncSelection selection) =>
        ConnectorHost.Build(new HelmPaths(Data), new PlainSecretProtector(), NullLoggerFactory.Instance, selection);

    private static async Task<JsonObject> CallAsync(McpServer server, string tool, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), default);
        return reply!["result"]!.AsObject();
    }

    private Task<(int Exit, string Output, string Error)> RunAsync(params string[] args) => RunAsync(args, new Dictionary<string, string>());

    private static async Task<(int Exit, string Output, string Error)> RunAsync(string[] args, Dictionary<string, string> env, Stream? input = null,
        Stream? output = null)
    {
        var (text, error) = (new StringWriter(), new StringWriter());
        var console = new CliConsole(text, error, env.GetValueOrDefault)
        {
            OpenInput = () => input ?? new MemoryStream(),
            OpenOutput = () => output ?? new MemoryStream(),
            ReadSecret = _ => null,
        };
        var exit = await ConnectorApp.RunAsync(args, console);
        return (exit, text.ToString(), error.ToString());
    }

    private static string Token()
    {
        var body = $"{SyncToken.Prefix}{new string('a', 16)}_{new string('b', 40)}";
        return body + SyncToken.Checksum(body);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _pending;

        public List<(string Url, string Body)> Requests { get; } = [];

        /// <summary>Waits for the pings sent without waiting (fire and forget) to arrive.</summary>
        public async Task Settled()
        {
            for (var i = 0; i < 100 && Volatile.Read(ref _pending) > 0; i++) await Task.Delay(20);
            await Task.Delay(50);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _pending);
            try
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                lock (Requests) Requests.Add((request.RequestUri!.ToString(), body));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }
}
