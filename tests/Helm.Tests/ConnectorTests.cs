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
        // Helm has no AI of its own since 0.96: a new box does not sync the old AI records.
        Assert.Equal(["wallet.", "tracker.", "habits."], SyncSelection.Resolve(null, Data).Groups);
        Assert.Equal(["wallet.", "tracker.", "habits."], SyncSelection.Resolve(null, Data).Prefixes);

        File.WriteAllText(Path.Combine(Data, SyncSelection.FileName), """{ "sync": ["wallet", "Notes."] }""");
        Assert.Equal(["wallet.", "notes."], SyncSelection.Resolve(null, Data).Prefixes);
        Assert.Equal(["tracker.", "missions."], SyncSelection.Resolve(" tracker , missions", Data).Groups);

        Assert.Throws<FormatException>(() => SyncSelection.Resolve("vault", Data));
        Assert.Throws<FormatException>(() => SyncSelection.Resolve("", Data));
        File.WriteAllText(Path.Combine(Data, SyncSelection.FileName), "{ sync: ");
        Assert.Throws<FormatException>(() => SyncSelection.Resolve(null, Data));
    }

    [Fact]
    public async Task A_connector_json_that_lists_ai_keeps_working_and_syncs_the_old_ai_records_as_before()
    {
        // Fairy's box, as written before 0.96: the connector must not stop after it updates itself.
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, SyncSelection.FileName), """{ "sync": ["ai", "wallet", "tracker", "habits", "vocab", "novel"] }""");

        var selection = SyncSelection.Resolve(null, Data);

        Assert.Equal(["ai.", "wallet.", "tracker.", "habits.", "vocab.", "novel."], selection.Groups);
        Assert.Equal(["ai.", "wallet.", "tracker.", "habits.", "vocab.", "novel.", "settings.ai-features"], selection.Prefixes);
        Assert.Equal(["ai.", "wallet."], SyncSelection.Resolve("ai.,wallet", Data).Groups);

        await using var services = Tools(selection);
        var tools = ConnectorMcp.Tools(services.GetServices<IMcpToolProvider>(), new ConfirmationGate()).Select(t => t.Name).ToHashSet();
        Assert.Contains("wallet_unfiled", tools);
        Assert.Contains("novel_suggest_translations", tools);
        Assert.DoesNotContain(tools, t => t.StartsWith("ai_", StringComparison.Ordinal));
        // The old records stay known to sync with their old policies: none is an unknown collection, the keys' deletions stay held.
        var descriptors = services.GetServices<SyncCollectionDescriptor>().ToDictionary(d => d.Name);
        Assert.All(LeftoverAiCollections.All, name => Assert.True(descriptors.ContainsKey(name), name));
        Assert.Equal(["ai.autoruns", "ai.briefs", "ai.jobs", "ai.keys", "ai.management", "ai.prompts", "ai.usage", "ai.workers", "settings.ai-features"],
            LeftoverAiCollections.All.Order(StringComparer.Ordinal));
        Assert.NotNull(descriptors["ai.keys"].ChangeGuard);
        Assert.NotNull(descriptors["ai.management"].ChangeGuard);
        Assert.Null(descriptors["ai.jobs"].ChangeGuard);
        Assert.Equal(SyncConflictPolicy.RemoteWins, descriptors["ai.usage"].Policy);
        Assert.Equal(SyncConflictPolicy.LastWriterWins, descriptors["settings.ai-features"].Policy);
        Assert.Contains(services.GetServices<SyncGroup>(), g => g.Prefix == "ai.");
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
        Assert.Contains("Syncs      Wallet (wallet.), Tracker (tracker.), Habits (habits.)", output, StringComparison.Ordinal);
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
        Assert.Contains($"more than {PushBreaker.MaxPushesPerMinute} pushes within a minute", reason, StringComparison.Ordinal);
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
    public async Task TheBreakerAlsoStopsALoopSlowerThanItsMinuteLimit()
    {
        var time = new ManualTime(Now);
        var breaker = new PushBreaker(new FakeSyncServer().Connect(), time);
        PushItem[] items = [new("wallet.items", "x", 0, false, [1])];

        // About thirty a minute (a write every second, debounced): under the minute's limit, over ten minutes' one.
        var pushes = 0;
        while (breaker.TrippedReason is null && pushes < 1000)
        {
            try { await breaker.PushAsync(items, default); }
            catch (SyncPushBreakerException) { }
            pushes++;
            time.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Contains($"{PushBreaker.MaxPushesPerTenMinutes} pushes within ten minutes", breaker.TrippedReason, StringComparison.Ordinal);
        Assert.InRange(pushes, PushBreaker.MaxPushesPerTenMinutes, PushBreaker.MaxPushesPerTenMinutes + 1);
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
    public async Task ByDefaultItServesWalletTrackerHabitsButNeverVault()
    {
        await using var services = Tools(SyncSelection.Resolve(null, Data));
        var tools = ConnectorMcp.Tools(services.GetServices<IMcpToolProvider>(), new ConfirmationGate()).Select(t => t.Name).ToHashSet();

        foreach (var tool in new[] { "tracker_add_task", "wallet_add_debt", "wallet_summary", "habit_check" }) Assert.Contains(tool, tools);
        Assert.DoesNotContain(tools, t => t.StartsWith("notes_", StringComparison.Ordinal) || t.StartsWith("mission", StringComparison.Ordinal)
            || t.StartsWith("vocab_", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, t => t.Contains("vault", StringComparison.OrdinalIgnoreCase));
        // Helm has no AI of its own (Fairy does that work): no ai_ tool.
        Assert.DoesNotContain(tools, t => t.StartsWith("ai_", StringComparison.Ordinal));
        Assert.DoesNotContain(services.GetServices<IMcpToolProvider>(), p => p.GetType().Namespace?.Contains("Vault", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(services.GetServices<SyncGroup>(), g => g.Prefix == "vault.");
    }

    [Fact]
    public async Task WithNovelItServesTheReportsAndNamesButNoDictionariesOrVoices()
    {
        await using var services = Tools(SyncSelection.Resolve("ai,wallet,tracker,habits,vocab,novel", Data));
        var server = ConnectorMcp.Server(services.GetServices<IMcpToolProvider>(), new ConfirmationGate(), NullLogger.Instance);

        foreach (var tool in new[] { "novel_translation_reports", "novel_names", "novel_suggest_translations" }) Assert.Contains(tool, server.ToolNames);
        Assert.Contains(services.GetServices<SyncGroup>(), g => g.Prefix == "novel.");
        // Fairy suggests translations of the sentences the reader marked; the dictionaries (big files) never come here
        Assert.Null(services.GetService<Helm.Modules.NovelReader.Dictionaries.DictionaryLibrary>());
        var reports = await CallAsync(server, "novel_translation_reports", new JsonObject());
        Assert.False(reports["isError"]!.GetValue<bool>());
        var listed = JsonNode.Parse(reports["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(0, listed["total"]!.GetValue<int>());
        Assert.False(listed["dictionaries_loaded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ADeletionOnlyDescribesItselfUntilTheUserConfirmsIt()
    {
        await using var services = Tools(SyncSelection.Resolve("ai,wallet,tracker,habits,notes,missions,vocab", Data));
        var gate = new ConfirmationGate();
        var server = ConnectorMcp.Server(services.GetServices<IMcpToolProvider>(), gate, NullLogger.Instance);
        var notes = services.GetRequiredService<Helm.Modules.Notes.NotesStore>();
        Assert.Contains("vocab_add_words", server.ToolNames);
        Assert.Equal(["mission_replan", "notes_trash"],
            services.GetServices<IMcpToolProvider>().SelectMany(p => p.Tools).Where(t => t.Destructive).Select(t => t.Name).Order());
        var id = await CreateNoteAsync(server, "Shopping list");

        var asked = await CallAsync(server, "notes_trash", new JsonObject { ["id"] = id });

        // Nothing happened; the model reads what would, and not the id that makes it happen.
        Assert.False(asked["isError"]!.GetValue<bool>());
        Assert.Empty(notes.Trash());
        var text = asked["content"]![0]!["text"]!.GetValue<string>();
        var shown = JsonNode.Parse(text)!;
        Assert.Equal("needs_confirmation", shown["status"]!.GetValue<string>());
        Assert.Contains("“Shopping list”", shown["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        var confirmation = ConfirmationId(asked);
        Assert.DoesNotContain(confirmation, text, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmationId", text, StringComparison.Ordinal);

        var done = await RpcAsync(server, ConfirmationGate.Confirm, new JsonObject { ["confirmationId"] = confirmation });
        Assert.False(done["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("\"trashed\":true", done["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Single(notes.Trash());

        var again = await RpcAsync(server, ConfirmationGate.Confirm, new JsonObject { ["confirmationId"] = confirmation });
        Assert.Contains("already used", again["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        var unknown = await CallAsync(server, "notes_trash", new JsonObject { ["id"] = "no-such-note" });
        Assert.True(unknown["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AConfirmationRunsOnceOnlyForTheSameCallWithinFiveMinutes()
    {
        var time = new ManualTime(Now);
        var gate = new ConfirmationGate(time);
        var runs = new List<string>();
        var bulk = new McpTool("wallet_delete_month", "Deletes a month of transactions.", McpTool.NoArguments(), (args, _) =>
        {
            runs.Add(args.GetProperty("month").GetString()!);
            return Task.FromResult<object?>(new { deleted = 42 });
        }) { Destructive = true };
        var server = ConnectorMcp.Server([new Provider([bulk])], gate, NullLogger.Instance);
        async Task<string> Ask(string month) => ConfirmationId(await CallAsync(server, bulk.Name, new JsonObject { ["month"] = month }));

        // Expired: nothing runs.
        var late = await Ask("2026-10");
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var expired = await RpcAsync(server, ConfirmationGate.Confirm, new JsonObject { ["confirmationId"] = late });
        Assert.Contains("expired", expired["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // Other arguments than the ones the user saw: nothing runs, and the id is spent.
        var tampered = await Ask("2026-10");
        var other = await RpcAsync(server, ConfirmationGate.Confirm,
            new JsonObject { ["confirmationId"] = tampered, ["name"] = bulk.Name, ["arguments"] = new JsonObject { ["month"] = "2026-09" } });
        Assert.Contains("does not match", other["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("already used", (await RpcAsync(server, ConfirmationGate.Confirm, new JsonObject { ["confirmationId"] = tampered }))["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // Cancelled: nothing runs.
        var declined = await Ask("2026-10");
        var cancelled = await RpcAsync(server, ConfirmationGate.Cancel, new JsonObject { ["confirmationId"] = declined });
        Assert.True(cancelled["result"]!["cancelled"]!.GetValue<bool>());
        Assert.NotNull((await RpcAsync(server, ConfirmationGate.Confirm, new JsonObject { ["confirmationId"] = declined }))["error"]);
        Assert.Empty(runs);

        // The same call, keys in another order: it runs, once.
        var yes = await Ask("2026-10");
        var pending = await RpcAsync(server, ConfirmationGate.List, null);
        Assert.Equal(yes, pending["result"]!["pending"]![0]!["confirmationId"]!.GetValue<string>());
        var ran = await RpcAsync(server, ConfirmationGate.Confirm,
            new JsonObject { ["confirmationId"] = yes, ["name"] = bulk.Name, ["arguments"] = new JsonObject { ["month"] = "2026-10" } });
        Assert.Contains("42", ran["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        await RpcAsync(server, ConfirmationGate.Confirm, new JsonObject { ["confirmationId"] = yes });
        Assert.Equal(["2026-10"], runs);
    }

    [Fact]
    public async Task TheConfirmMethodsAreNoTools()
    {
        var server = ConnectorMcp.Server([], new ConfirmationGate(), NullLogger.Instance);

        var list = await RpcAsync(server, "tools/list", null);
        var call = await RpcAsync(server, "tools/call", new JsonObject { ["name"] = ConfirmationGate.Confirm, ["arguments"] = new JsonObject() });

        Assert.DoesNotContain("helm/", list.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Unknown tool", call["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
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

        await using var services = Tools(SyncSelection.Resolve("notes", Data));
        var gate = new ConfirmationGate();
        using var host = new McpSocketHost(socket, () => ConnectorMcp.Server(services.GetServices<IMcpToolProvider>(), gate, NullLogger.Instance),
            NullLogger.Instance);
        host.Start();
        Assert.Throws<InvalidOperationException>(() => new McpSocketHost(socket, () => throw new InvalidOperationException(), NullLogger.Instance).Start());

        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"fairy-core"}}}""" + "\n"
            + """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\n"
            // The host's own methods go through the relay too.
            + """{"jsonrpc":"2.0","id":3,"method":"helm/pending"}""" + "\n"));
        var output = new MemoryStream();
        var (exit, _, _) = await RunAsync(["--data-dir", Data, "mcp"], new() { ["HELM_MCP_SOCKET"] = socket }, input, output);

        Assert.Equal(0, exit);
        var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(3, replies.Count);
        Assert.Empty(replies[2]["result"]!["pending"]!.AsArray());
        Assert.Equal("helm", replies[0]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains(replies[1]["result"]!["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "notes_create");

        // The user's answer from a terminal, to a deletion asked for on another connection.
        var other = ConnectorMcp.Server(services.GetServices<IMcpToolProvider>(), gate, NullLogger.Instance);
        var note = await CreateNoteAsync(other, "Old ideas");
        var waiting = ConfirmationId(await CallAsync(other, "notes_trash", new JsonObject { ["id"] = note }));
        var env = new Dictionary<string, string> { ["HELM_MCP_SOCKET"] = socket };
        var (listed, list, _) = await RunAsync(["--data-dir", Data, "pending"], env);
        Assert.Equal(0, listed);
        Assert.Contains(waiting, list, StringComparison.Ordinal);
        Assert.Contains("“Old ideas”", list, StringComparison.Ordinal);
        var (confirmed, result, _) = await RunAsync(["--data-dir", Data, "confirm", waiting], env);
        Assert.Equal(0, confirmed);
        Assert.Contains("\"trashed\":true", result, StringComparison.Ordinal);
        var (twice, _, error2) = await RunAsync(["--data-dir", Data, "confirm", waiting], env);
        Assert.Equal(ConnectorApp.Failed, twice);
        Assert.Contains("already used", error2, StringComparison.Ordinal);
        var (cancelUnknown, _, _) = await RunAsync(["--data-dir", Data, "cancel", "nope"], env);
        Assert.Equal(ConnectorApp.Failed, cancelUnknown);
    }

    private ServiceProvider Tools(SyncSelection selection) =>
        ConnectorHost.Build(new HelmPaths(Data), new PlainSecretProtector(), NullLoggerFactory.Instance, selection);

    private static async Task<string> CreateNoteAsync(McpServer server, string title)
    {
        var created = await CallAsync(server, "notes_create", new JsonObject { ["title"] = title, ["text"] = "from Fairy Core" });
        return JsonNode.Parse(created["content"]![0]!["text"]!.GetValue<string>())!["id"]!.GetValue<string>();
    }

    /// <summary>The id the host reads in the result's _meta (never in its text).</summary>
    private static string ConfirmationId(JsonObject result) =>
        result["_meta"]![ConfirmationGate.MetaKey]!["confirmationId"]!.GetValue<string>();

    private static async Task<JsonObject> RpcAsync(McpServer server, string method, JsonObject? parameters)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = method, ["params"] = parameters };
        return (await server.HandleAsync(request.ToJsonString(), default))!;
    }

    private sealed class Provider(IEnumerable<McpTool> tools) : IMcpToolProvider
    {
        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools => tools;
    }

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
