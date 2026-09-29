using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Helm's MCP server: the JSON-RPC protocol, the notes and Tracker tools, and the named pipe.</summary>
public sealed class McpTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));
    private readonly NotesStore _notes;
    private readonly TrackerStore _tracker;
    private readonly LinkHub _links;

    public McpTests()
    {
        _notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        _tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        _links = new LinkHub(new MemorySynced<HelmLink>(), () =>
            [new NoteLinkProvider(_notes, _ => { }), new TaskLinkProvider(_tracker, (_, _) => { }), new PersonLinkProvider(_tracker, (_, _) => { })], _time);
    }

    private McpServer Server(bool readOnly = false) =>
        new(new IMcpToolProvider[] { new NotesMcpTools(_notes, _links), new TrackerMcpTools(_tracker) }.SelectMany(p => p.Tools).Where(t => !readOnly || t.ReadOnly),
            "0.17.0", McpEndpoint.Instructions);

    [Fact]
    public async Task Initialize_agrees_on_a_protocol_version_and_offers_tools()
    {
        var server = Server();
        var reply = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"claude-code","version":"2"}}}""", default);
        Assert.Equal("2025-03-26", reply!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("helm", reply["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(reply["result"]!["capabilities"]!["tools"]);

        var unknownVersion = await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""", default);
        Assert.Equal(McpServer.ProtocolVersions[0], unknownVersion!["result"]!["protocolVersion"]!.GetValue<string>());

        Assert.Null(await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", default));
        var ping = await server.HandleAsync("""{"jsonrpc":"2.0","id":"p","method":"ping"}""", default);
        Assert.Equal("p", ping!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_list_every_tool_with_a_schema_and_never_the_vault()
    {
        var reply = await Server().HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", default);
        var tools = reply!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("notes_search", tools);
        Assert.Contains("tracker_add_task", tools);
        Assert.DoesNotContain(tools, t => t.Contains("vault", StringComparison.OrdinalIgnoreCase));
        Assert.All(reply["result"]!["tools"]!.AsArray(), t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));

        var readOnly = await Server(readOnly: true).HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", default);
        var names = readOnly!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["notes_read", "notes_search", "tracker_debts", "tracker_lists", "tracker_tasks"], names);
    }

    [Fact]
    public async Task Bad_requests_get_json_rpc_errors_and_bad_calls_readable_tool_errors()
    {
        var server = Server();
        Assert.Equal(-32700, (await server.HandleAsync("{not json", default))!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, (await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""", default))!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, (await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nope"}}""", default))!["error"]!["code"]!.GetValue<int>());

        var call = await Call(server, "notes_read", new { id = "missing" });
        Assert.True(call.IsError);
        Assert.Contains("There is no note missing", call.Text);
        var badDate = await Call(server, "tracker_add_task", new { title = "x", due = "tomorrow" });
        Assert.True(badDate.IsError);
    }

    [Fact]
    public async Task Claude_can_write_find_edit_and_link_notes()
    {
        var server = Server();
        var created = await Call(server, "notes_create", new { title = "Trip", text = "Book flights" });
        var id = JsonDocument.Parse(created.Text).RootElement.GetProperty("id").GetString()!;

        var found = await Call(server, "notes_search", new { query = "flight" });
        Assert.Contains(id, found.Text);
        await Call(server, "notes_edit", new { id, append = "Hotel near the beach" });
        Assert.Equal("Book flights\nHotel near the beach", _notes.Get(id)!.Body);

        var list = _tracker.AddWorkspace("Home", WorkspaceKind.Tasks);
        var task = _tracker.AddItem(list, new TrackerItemDraft("Pack"));
        var linked = await Call(server, "notes_link", new { id, task_id = task });
        Assert.False(linked.IsError, linked.Text);
        var read = await Call(server, "notes_read", new { id });
        Assert.Contains("\"Pack\"", read.Text);

        await Call(server, "notes_trash", new { id });
        Assert.True(_notes.Get(id)!.Trashed);
        Assert.True((await Call(server, "notes_edit", new { id, text = "x" })).IsError);
    }

    [Fact]
    public async Task Claude_can_manage_tasks_and_the_debt_book()
    {
        var server = Server();
        _tracker.AddWorkspace("Work", WorkspaceKind.Tasks);
        _tracker.AddWorkspace("Debts", WorkspaceKind.Debts);

        var added = await Call(server, "tracker_add_task", new { title = "Ship", list = "work", priority = "high", due = "2026-10-01T09:30", subtasks = new[] { "Tests", "Notes" } });
        Assert.False(added.IsError, added.Text);
        var id = JsonDocument.Parse(added.Text).RootElement.GetProperty("id").GetString()!;
        var item = _tracker.GetItem(id)!;
        Assert.Equal((TrackerPriority.High, new DateOnly(2026, 10, 1)), (item.Priority, item.DueDate));
        Assert.Equal(2, _tracker.Subtasks(id).Count);

        var due = await Call(server, "tracker_tasks", new { due_before = "2026-10-02" });
        Assert.Contains("Ship", due.Text);
        Assert.Contains("Tests", due.Text);
        Assert.DoesNotContain("Ship", (await Call(server, "tracker_tasks", new { due_before = "2026-09-30" })).Text);

        await Call(server, "tracker_update_task", new { id, title = "Ship v1", clear_due = true });
        Assert.Null(_tracker.GetItem(id)!.DueAt);
        await Call(server, "tracker_complete_task", new { id });
        Assert.True(_tracker.GetItem(id)!.IsCompleted);
        Assert.DoesNotContain("Ship v1", (await Call(server, "tracker_tasks", new { })).Text);
        Assert.Contains("Ship v1", (await Call(server, "tracker_tasks", new { status = "done" })).Text);
        await Call(server, "tracker_reopen_task", new { id });
        Assert.False(_tracker.GetItem(id)!.IsCompleted);

        var debt = await Call(server, "tracker_add_debt", new { person = "Nam", amount = "150k", kind = "owes_me", note = "lunch" });
        Assert.False(debt.IsError, debt.Text);
        await Call(server, "tracker_add_debt", new { person = "nam", amount = 50000, kind = "repayment" });
        var debts = await Call(server, "tracker_debts", new { });
        Assert.Contains("\"balance\":100000", debts.Text);
        Assert.Contains("owes the user", debts.Text);
        Assert.True((await Call(server, "tracker_add_debt", new { person = "Nam", amount = "lots", kind = "owes_me" })).IsError);
    }

    [Fact]
    public async Task The_pipe_serves_a_connection_per_client_and_honours_the_off_switch()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        var enabled = true;
        using var host = new McpPipeHost(paths, () => enabled ? Server() : null, NullLogger<McpPipeHost>.Instance);
        host.Start();

        var reply = await RoundTrip(paths, """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""");
        Assert.Contains("notes_search", reply);
        Assert.Contains("\"id\":7", reply);

        enabled = false;
        await using var pipe = new NamedPipeClientStream(".", McpEndpoint.PipeName(paths), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000);
        var buffer = new byte[16];
        // Turned off: the server closes the connection without answering.
        var read = await pipe.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);
    }

    [Fact]
    public void Pipe_names_differ_per_data_folder()
    {
        Assert.Equal(McpEndpoint.PipeName(new HelmPaths(@"C:\A\Helm")), McpEndpoint.PipeName(new HelmPaths(@"c:\a\helm\")));
        Assert.NotEqual(McpEndpoint.PipeName(new HelmPaths(@"C:\A\Helm")), McpEndpoint.PipeName(new HelmPaths(@"C:\B\Helm")));
        Assert.StartsWith("Helm.Mcp.", McpEndpoint.PipeName(new HelmPaths()));
    }

    [Fact]
    public void The_client_config_starts_this_helm_with_mcp_and_the_data_folder_of_a_test_copy()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        using var settings = new SettingsStoreFactory(paths);
        var config = new McpClientConfig(paths, settings);
        Assert.Equal(["--mcp", "--data-dir", dir.Path], config.Arguments);
        Assert.Contains("claude mcp add --scope user helm -- ", config.ClaudeCodeCommand);
        var file = config.WriteConfigFile()!;
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("stdio", json["mcpServers"]!["helm"]!["type"]!.GetValue<string>());
        Assert.Equal("--mcp", json["mcpServers"]!["helm"]!["args"]![0]!.GetValue<string>());

        settings.Get<McpSettings>(McpSettings.StoreId).Update(s => s.Enabled = false);
        Assert.Null(config.WriteConfigFile());
    }

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string tool, object args)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(JsonSerializer.Serialize(args)) },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), default);
        var result = reply!["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    private static async Task<string> RoundTrip(HelmPaths paths, string line)
    {
        await using var pipe = new NamedPipeClientStream(".", McpEndpoint.PipeName(paths), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "";
    }
}
