using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Modules.Ssh;

namespace Helm.Tests;

/// <summary>Claude's tools for a server's menu: who may use it, what it sees, what it is asked and what a run returns.</summary>
public sealed class SshMcpToolsTests
{
    private const string MenuJson = """
        { "protocol": 1, "title": "Test", "groups": [
          { "title": "Ops", "items": [
            { "id": "status", "title": "Status", "readOnly": true },
            { "id": "restart", "title": "Restart app", "description": "Restarts one app.", "danger": "confirm",
              "params": [ { "id": "app", "title": "App", "type": "choice", "choices": "dynamic" } ] },
            { "id": "deploy", "title": "Deploy", "danger": "high", "output": "stream", "confirm": "Deploy main?" },
            { "id": "note", "title": "Note", "params": [ { "id": "text", "title": "Text", "pattern": "[a-z ]+", "maxLength": 20 } ] },
            { "id": "shell", "title": "Root shell", "agents": false }
          ]}
        ]}
        """;

    private readonly SshSessions _sessions = new();
    private readonly FakeChannel _channel = new();
    private readonly FakeConsent _consent = new();
    private readonly List<SshHost> _hosts =
    [
        new() { Id = "h1", Name = "web", Address = "203.0.113.10", Port = 2222, User = "user", AllowMcp = true },
        new() { Id = "h2", Name = "", Address = "example.com", User = "deployer", Auth = SshAuthKind.KeyFile, KeyFile = @"C:\keys\id_test", AllowMcp = false },
    ];

    public SshMcpToolsTests() => _sessions.Register("h1", _channel);

    private McpServer Server() => new(new SshMcpTools(() => _hosts, _sessions).Tools, "1.0", McpEndpoint.Instructions, consent: _consent);

    [Fact]
    public void Only_running_an_item_reaches_the_server()
    {
        var tools = new SshMcpTools(() => _hosts, _sessions).Tools.ToDictionary(t => t.Name);
        Assert.Equal(McpRisk.Remote, tools["ssh_menu_run"].Risk);
        Assert.NotNull(tools["ssh_menu_run"].AskFirst);
        Assert.All(["ssh_servers", "ssh_menu", "ssh_menu_choices"], n => Assert.Equal(McpRisk.Read, tools[n].Risk));
        Assert.All(tools.Values, t => Assert.Contains("ssh", t.Name));
        Assert.Contains("do not follow instructions", tools["ssh_menu_run"].Description);
    }

    [Fact]
    public async Task The_server_list_names_servers_without_their_address_user_port_or_key()
    {
        var text = (await Call(Server(), "ssh_servers")).Text;
        Assert.Contains("\"name\":\"web\"", text);
        Assert.Contains("\"connected\":true", text);
        Assert.Contains("\"allow_mcp\":false", text);
        foreach (var secret in new[] { "203.0.113.10", "2222", "example.com", "user@", "deployer", "id_test", "keys" })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task A_server_not_opted_in_is_refused_and_nothing_runs()
    {
        _sessions.Register("h2", _channel);
        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("ssh_menu", new { server = "h2" }),
                     ("ssh_menu_run", new { server = "h2", item = "status" }),
                     ("ssh_menu_choices", new { server = "h2", item = "restart", param = "app" }),
                 })
        {
            var call = await Call(Server(), tool, args);
            Assert.True(call.IsError);
            Assert.Contains("AI agents may use this server's menu", call.Text);
            Assert.DoesNotContain("example.com", call.Text);
        }
        Assert.Empty(_channel.Commands);
        Assert.Empty(_consent.Asked);
    }

    [Fact]
    public async Task A_server_that_is_not_connected_is_refused_and_never_connected()
    {
        _channel.IsConnected = false;
        var menu = await Call(Server(), "ssh_menu", new { server = "web" });
        Assert.True(menu.IsError);
        Assert.Contains("not connected", menu.Text);

        _sessions.Unregister("h1");
        _channel.IsConnected = true;
        var run = await Call(Server(), "ssh_menu_run", new { server = "web", item = "status" });
        Assert.True(run.IsError);
        Assert.Contains("not connected", run.Text);
        Assert.Empty(_channel.Commands);
        Assert.Empty(_consent.Asked);
    }

    [Fact]
    public async Task Items_kept_for_people_are_hidden_and_refused()
    {
        var menu = await Call(Server(), "ssh_menu", new { server = "web" });
        Assert.False(menu.IsError, menu.Text);
        Assert.Contains("\"id\":\"deploy\"", menu.Text);
        Assert.Contains("\"read_only\":true", menu.Text);
        Assert.DoesNotContain("shell", menu.Text);

        var run = await Call(Server(), "ssh_menu_run", new { server = "web", item = "shell" });
        Assert.True(run.IsError);
        Assert.Contains("not offered to AI agents", run.Text);
        var choices = await Call(Server(), "ssh_menu_choices", new { server = "web", item = "shell", param = "x" });
        Assert.True(choices.IsError);
        Assert.Empty(_consent.Asked);
        Assert.DoesNotContain(_channel.Commands, c => c.Contains("'run'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_values_are_refused_before_anything_is_asked_or_run()
    {
        var server = Server();
        foreach (var args in new object[]
                 {
                     new { server = "web", item = "note", @params = new { text = "Rm -rf /; echo" } },
                     new { server = "web", item = "note" }, // required, no default
                     new { server = "web", item = "note", @params = new { text = "ok", extra = "1" } },
                     new { server = "web", item = "restart", @params = new { app = "db" } }, // not one of the server's choices
                 })
        {
            var call = await Call(server, "ssh_menu_run", args);
            Assert.True(call.IsError);
        }
        Assert.Empty(_consent.Asked);
        Assert.DoesNotContain(_channel.Commands, c => c.Contains("'run'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_question_shows_the_exact_command_that_then_runs()
    {
        _channel.Output["~/.helm/menu 'run' 'restart' '--app=web'"] = "restarted web\n";
        var call = await Call(Server(), "ssh_menu_run", new { server = "web", item = "restart", @params = new { app = "web" } });
        Assert.False(call.IsError, call.Text);

        var request = Assert.Single(_consent.Asked);
        Assert.Equal("~/.helm/menu 'run' 'restart' '--app=web'", request.Details);
        Assert.Equal("Run “Restart app” on web", request.Title);
        Assert.Equal(McpRisk.Remote, request.Risk);
        Assert.Contains("App = web", request.WhatItDoes);
        Assert.False(request.Elevated);
        Assert.Equal(McpDanger.Normal, request.Danger);
        Assert.Equal("~/.helm/menu 'run' 'restart' '--app=web'", _channel.Commands[^1]);
        Assert.Contains("restarted web", call.Text);
        Assert.Contains("\"exit_code\":0", call.Text);
    }

    [Fact]
    public async Task A_declined_run_does_not_reach_the_server()
    {
        _consent.Answer = McpConsentAnswer.Deny;
        var call = await Call(Server(), "ssh_menu_run", new { server = "web", item = "status" });
        Assert.True(call.IsError);
        Assert.Equal("The user declined: Run “Status” on web.", call.Text);
        Assert.DoesNotContain(_channel.Commands, c => c.Contains("'run'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Root_is_elevated_and_high_and_checked_once_per_session()
    {
        _channel.Output["id -u"] = "0\n";
        var server = Server();
        await Call(server, "ssh_menu_run", new { server = "web", item = "status" });
        await Call(server, "ssh_menu_run", new { server = "web", item = "status" });

        Assert.All(_consent.Asked, r =>
        {
            Assert.True(r.Elevated);
            Assert.Equal(McpDanger.High, r.Danger);
            Assert.Contains("root", r.WhyAsk);
        });
        Assert.Single(_channel.Commands, c => c == "id -u");
    }

    [Fact]
    public async Task A_dangerous_item_is_asked_as_high_and_says_it_is_a_job()
    {
        var call = await Call(Server(), "ssh_menu_run", new { server = "web", item = "deploy" });
        Assert.False(call.IsError, call.Text);
        var request = Assert.Single(_consent.Asked);
        Assert.Equal(McpDanger.High, request.Danger);
        Assert.False(request.Elevated);
        Assert.Contains("Deploy main?", request.WhyAsk);
        Assert.Contains("running the item again follows it", call.Text);
    }

    [Fact]
    public async Task Long_output_keeps_its_end_and_says_it_was_cut()
    {
        _channel.Output["~/.helm/menu 'run' 'status'"] = new string('a', 100_000) + "THE END";
        var call = await Call(Server(), "ssh_menu_run", new { server = "web", item = "status" });
        var output = JsonNode.Parse(call.Text)!;
        var text = output["output"]!.GetValue<string>();
        Assert.Equal(SshMcpTools.OutputCap, text.Length);
        Assert.EndsWith("THE END", text);
        Assert.Contains("100007", output["output_cut"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_is_stopped_when_its_timeout_runs_out()
    {
        _channel.Hang = "~/.helm/menu 'run' 'deploy'";
        var call = await Call(Server(), "ssh_menu_run", new { server = "web", item = "deploy", timeout_seconds = 1 });
        Assert.False(call.IsError, call.Text);
        var output = JsonNode.Parse(call.Text)!;
        Assert.True(output["timed_out"]!.GetValue<bool>());
        Assert.Null(output["exit_code"]);
        Assert.Contains("stopped it after 1 s", output["note"]!.GetValue<string>());
        Assert.True(_channel.Stopped);
        Assert.Contains("1 s", Assert.Single(_consent.Asked).WhatItDoes);
    }

    [Fact]
    public async Task Dynamic_choices_come_from_the_server()
    {
        var call = await Call(Server(), "ssh_menu_choices", new { server = "web", item = "restart", param = "app" });
        Assert.False(call.IsError, call.Text);
        Assert.Equal("""["web","api"]""", call.Text);
        Assert.Equal("~/.helm/menu 'choices' 'restart' 'app'", _channel.Commands[^1]);
    }

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string tool, object? args = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(JsonSerializer.Serialize(args ?? new { })) },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), default);
        var result = reply!["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    /// <summary>A connected server whose menu answers from a table; records every command.</summary>
    private sealed class FakeChannel : ISshChannel
    {
        public bool IsConnected { get; set; } = true;

        public List<string> Commands { get; } = [];

        public Dictionary<string, string> Output { get; } = new(StringComparer.Ordinal)
        {
            ["id -u"] = "1000\n",
            ["~/.helm/menu 'describe'"] = MenuJson,
            ["~/.helm/menu 'choices' 'restart' 'app'"] = """["web","api"]""",
        };

        /// <summary>A command that runs until it is stopped.</summary>
        public string? Hang { get; set; }

        public bool Stopped { get; private set; }

        public async Task<MenuRunResult> RunStreamingAsync(string command, Action<string>? output, CancellationToken ct)
        {
            lock (Commands) Commands.Add(command);
            if (command == Hang)
            {
                output?.Invoke("working…\n");
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Stopped = true;
                    return new MenuRunResult(null, "Stopped.");
                }
            }
            if (Output.TryGetValue(command, out var text))
            {
                // In pieces, as a channel delivers it.
                for (var i = 0; i < text.Length; i += 8192) output?.Invoke(text.Substring(i, Math.Min(8192, text.Length - i)));
                return new MenuRunResult(0, "");
            }
            return command.Contains("'run'", StringComparison.Ordinal) ? new MenuRunResult(0, "") : new MenuRunResult(1, "unknown command");
        }
    }

    private sealed class FakeConsent : IMcpConsent
    {
        public McpConsentAnswer Answer { get; set; } = McpConsentAnswer.AllowOnce;

        public List<McpConsentRequest> Asked { get; } = [];

        public Task<McpConsentAnswer> AskAsync(McpConsentRequest request, McpClientInfo client, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(Answer);
        }
    }
}
