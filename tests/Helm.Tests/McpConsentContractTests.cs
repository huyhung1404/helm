using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Tests;

/// <summary>The MCP consent contract: each tool's risk, the question asked before a call, and who is calling.</summary>
public sealed class McpConsentContractTests
{
    private int _runs;

    private McpTool Tool(string name, bool readOnly = false, McpRisk? risk = null)
    {
        var tool = new McpTool(name, $"The {name} tool.", McpTool.NoArguments(), (_, _) =>
        {
            _runs++;
            return Task.FromResult<object?>("done");
        }) { ReadOnly = readOnly };
        return risk is { } r ? tool with { Risk = r } : tool;
    }

    [Fact]
    public void A_tools_risk_defaults_from_read_only()
    {
        Assert.Equal(McpRisk.Read, Tool("a", readOnly: true).Risk);
        Assert.Equal(McpRisk.Change, Tool("b").Risk);
        Assert.Equal(McpRisk.Remote, Tool("c", readOnly: true, risk: McpRisk.Remote).Risk);
    }

    [Fact]
    public async Task Remote_tools_tell_the_client_they_reach_another_machine()
    {
        var server = new McpServer([Tool("read", readOnly: true), Tool("remote", risk: McpRisk.Remote)], "1.0", "");
        var tools = (await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", default))!["result"]!["tools"]!.AsArray();
        var hints = tools.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["annotations"]!);
        Assert.True(hints["read"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(hints["read"]!["destructiveHint"]!.GetValue<bool>());
        Assert.False(hints["remote"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.True(hints["remote"]!["destructiveHint"]!.GetValue<bool>());
        Assert.True(hints["remote"]!["openWorldHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Without_a_consent_service_remote_tools_are_refused_and_changes_still_run()
    {
        var server = new McpServer([Tool("change"), Tool("remote", risk: McpRisk.Remote)], "1.0", "");

        var remote = await Call(server, "remote");
        Assert.True(remote.IsError);
        Assert.Equal(0, _runs);

        Assert.False((await Call(server, "change")).IsError);
        Assert.Equal(1, _runs);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task A_declined_call_does_not_run_and_tells_claude_why()
    {
        var consent = new FakeConsent(McpConsentAnswer.Deny);
        var server = new McpServer([Tool("change") with
        {
            AskFirst = (_, _) => Task.FromResult<McpConsentRequest?>(new McpConsentRequest(
                "change", McpRisk.Change, "Rename the list “Home”", "Renames it.", "It syncs.", null, "Home", false, McpDanger.Normal)),
        }], "1.0", "", consent: consent);

        var call = await Call(server, "change");
        Assert.True(call.IsError);
        Assert.Equal("The user declined: Rename the list “Home”.", call.Text);
        Assert.Equal(0, _runs);
        Assert.Equal("Rename the list “Home”", Assert.Single(consent.Asked).Request.Title);
    }

    [Fact]
    public async Task Consent_is_asked_for_every_call_that_is_not_read_only()
    {
        var consent = new FakeConsent(McpConsentAnswer.AllowOnce);
        var server = new McpServer([Tool("read", readOnly: true), Tool("change"), Tool("remote", risk: McpRisk.Remote)], "1.0", "", consent: consent);

        Assert.False((await Call(server, "read")).IsError);
        Assert.False((await Call(server, "change", new { list = "Home" })).IsError);
        Assert.False((await Call(server, "remote")).IsError);

        Assert.Equal(3, _runs);
        Assert.Equal(["change", "remote"], consent.Asked.Select(a => a.Request.Tool));
        // Without AskFirst the question is built from the tool, with the arguments as they came.
        var change = consent.Asked[0].Request;
        Assert.Equal((McpRisk.Change, "The change tool."), (change.Risk, change.WhatItDoes));
        Assert.Equal("""{"list":"Home"}""", change.Details);
        Assert.Equal(McpRisk.Remote, consent.Asked[1].Request.Risk);
    }

    [Fact]
    public async Task The_client_is_recorded_from_initialize()
    {
        var consent = new FakeConsent(McpConsentAnswer.AllowOnce);
        var server = new McpServer([Tool("change")], "1.0", "", consent: consent);
        Assert.Equal("Unknown client", server.Client.Name);
        var id = server.Client.ConnectionId;
        var changed = 0;
        server.Changed += (_, _) => changed++;

        await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"claude-code\u0007","version":"2.1"}}}""", default);
        Assert.Equal(new McpClientInfo(id, "claude-code", "2.1"), server.Client);

        await Call(server, "change");
        Assert.Equal(server.Client, Assert.Single(consent.Asked).Client);
        Assert.Equal(1, server.Calls);
        Assert.Equal(2, changed);
    }

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string tool, object? arguments = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonSerializer.SerializeToNode(arguments ?? new { }) },
        };
        var result = (await server.HandleAsync(request.ToJsonString(), default))!["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    private sealed class FakeConsent(McpConsentAnswer answer) : IMcpConsent
    {
        public List<(McpConsentRequest Request, McpClientInfo Client)> Asked { get; } = [];

        public Task<McpConsentAnswer> AskAsync(McpConsentRequest request, McpClientInfo client, CancellationToken ct)
        {
            Asked.Add((request, client));
            return Task.FromResult(answer);
        }
    }
}
