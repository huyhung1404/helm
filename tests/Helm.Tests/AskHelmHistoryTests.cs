using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;

namespace Helm.Tests;

/// <summary>
/// What Claude's chat sends back of a conversation: the history as it came while nothing changed, a recap instead once
/// the tools change (Claude refuses thinking written under other tools) or the history grows long, and a cache
/// breakpoint after the instructions. Every request keeps each tool_use paired with its tool_result.
/// </summary>
public sealed class AskHelmHistoryTests
{
    private readonly List<JsonObject> _sent = [];
    private readonly Queue<string> _answers = new();

    private static readonly AssistantTool Add = Tool("wallet_add_transaction");
    private static readonly AssistantTool Tasks = Tool("tracker_tasks", readOnly: true);

    [Fact]
    public async Task The_history_goes_back_as_it_came_with_each_call_paired_and_the_instructions_cached()
    {
        var chat = Chat(Add);
        _answers.Enqueue(Calls(("toolu_1", "wallet_add_transaction")));
        _answers.Enqueue(Text("Xong rồi Anh."));

        chat.AddUser("phở 45k");
        await chat.NextAsync(CancellationToken.None);
        chat.AddResults([new ToolOutcome("toolu_1", """{"id":"t1","amount":-45000}""", false)]);
        chat.AddUser("cảm ơn em");
        await chat.NextAsync(CancellationToken.None);

        var last = _sent[^1];
        var messages = last["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Contains("thinking", messages[1]!.ToJsonString());
        AssertPaired(messages);
        // The instructions have their own breakpoint; the conversation's moves with it (top level).
        Assert.Equal("ephemeral", (string?)last["system"]![0]!["cache_control"]!["type"]);
        Assert.Equal("ephemeral", (string?)last["cache_control"]!["type"]);
    }

    [Fact]
    public async Task New_tools_after_an_answer_start_the_conversation_again_from_a_recap()
    {
        var chat = Chat(Add);
        _answers.Enqueue(Calls(("toolu_1", "wallet_add_transaction")));
        _answers.Enqueue(Text("Anh có 2 việc."));

        chat.AddUser("phở 45k");
        await chat.NextAsync(CancellationToken.None);
        chat.AddResults([new ToolOutcome("toolu_1", """{"id":"t1","amount":-45000}""", false)]);
        chat.AddTools([Tasks]);
        chat.AddUser("hôm nay anh có việc gì?");
        await chat.NextAsync(CancellationToken.None);

        var last = _sent[^1];
        var message = (string)Assert.Single(last["messages"]!.AsArray())!["content"]!;
        // No thinking or call of before goes back under the new tools: what happened is in the words.
        Assert.DoesNotContain("thinking", message);
        Assert.DoesNotContain("tool_use", message);
        Assert.Contains("Earlier in this conversation", message);
        Assert.Contains("phở 45k", message);
        Assert.Contains("wallet_add_transaction answered", message);
        Assert.Contains("hôm nay anh có việc gì?", message);
        Assert.Equal(["wallet_add_transaction", "tracker_tasks"], last["tools"]!.AsArray().Select(t => (string?)t!["name"]));
    }

    [Fact]
    public async Task Tools_added_before_any_answer_keep_the_history()
    {
        var chat = Chat(Add);
        _answers.Enqueue(Text("Dạ."));

        chat.AddUser("phở 45k");
        chat.AddTools([Tasks]);
        await chat.NextAsync(CancellationToken.None);

        Assert.Equal("phở 45k", (string?)Assert.Single(_sent[^1]["messages"]!.AsArray())!["content"]);
    }

    [Fact]
    public async Task Web_search_turned_on_in_the_middle_of_a_round_goes_on_from_a_recap_without_an_unanswered_call()
    {
        var chat = Chat(Add);
        _answers.Enqueue(Calls(("toolu_1", "web_lookup")));
        _answers.Enqueue(Text("Hôm nay Hà Nội 25 độ."));

        chat.AddUser("thời tiết hôm nay?");
        await chat.NextAsync(CancellationToken.None);
        chat.AddWebSearch(3);
        chat.AddResults([new ToolOutcome("toolu_1", """{"web_search":"on"}""", false)]);
        await chat.NextAsync(CancellationToken.None);

        var messages = _sent[^1]["messages"]!.AsArray();
        var message = (string)Assert.Single(messages)!["content"]!;
        AssertPaired(messages);
        Assert.Contains("thời tiết hôm nay?", message);
        Assert.Contains("Go on from here.", message);
    }

    [Fact]
    public async Task A_long_history_starts_again_from_a_recap_that_keeps_the_latest()
    {
        var chat = Chat(Tasks);
        var big = new string('x', 3000);
        for (var turn = 0; turn < 12; turn++)
        {
            _answers.Enqueue(Calls(($"toolu_{turn}", "tracker_tasks")));
            _answers.Enqueue(Text($"answer {turn}"));
            chat.AddUser($"question {turn}");
            await chat.NextAsync(CancellationToken.None);
            chat.AddResults([new ToolOutcome($"toolu_{turn}", $"[\"{big}\"]", false)]);
            await chat.NextAsync(CancellationToken.None);
            AssertPaired(_sent[^1]["messages"]!.AsArray());
        }
        // Each call's body stays far under what twelve turns of results would make.
        Assert.True(_sent[^1].ToJsonString().Length < ChatRecap.MaxHistory + 3 * 3000 + 4000, $"{_sent[^1].ToJsonString().Length} characters.");
        Assert.Contains(_sent, body => body["messages"]!.AsArray().Count == 1 && body.ToJsonString().Contains("Earlier in this conversation"));
        Assert.Contains("answer 10", _sent[^1].ToJsonString());
    }

    /// <summary>Every tool_use has its tool_result in the next message, and every tool_result answers the message before.</summary>
    private static void AssertPaired(JsonArray messages)
    {
        static IEnumerable<string> Ids(JsonNode? message, string type) =>
            message?["content"] is JsonArray blocks ? blocks.Where(b => (string?)b!["type"] == type).Select(b => (string)b![type == "tool_use" ? "id" : "tool_use_id"]!) : [];
        for (var i = 0; i < messages.Count; i++)
        {
            var asked = Ids(messages[i], "tool_use").ToList();
            var answered = i + 1 < messages.Count ? Ids(messages[i + 1], "tool_result").ToList() : [];
            if (i + 1 < messages.Count) Assert.Equal(asked.Order(), answered.Order());
            if (i == 0) Assert.Empty(Ids(messages[0], "tool_result"));
        }
    }

    private IAssistantChat Chat(params AssistantTool[] tools)
    {
        var http = new HttpClient(new Fake(async request =>
        {
            _sent.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject());
            return _answers.Dequeue();
        }));
        return new ClaudeAssistantChatFactory(http).Create("sk-ant-test", "claude-haiku-5-5", "low", "Be Helm.", tools);
    }

    private static AssistantTool Tool(string name, bool readOnly = false) =>
        new(new McpTool(name, name, McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(null)) { ReadOnly = readOnly },
            readOnly ? AssistantToolMode.Read : AssistantToolMode.Auto);

    private static string Calls(params (string Id, string Name)[] calls) =>
        Answer("tool_use", calls.Select(c => new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = new JsonObject() }));

    private static string Text(string text) => Answer("end_turn", [new JsonObject { ["type"] = "text", ["text"] = text }]);

    /// <summary>A response with an (omitted) thinking block first, as Claude's 5.x models send them.</summary>
    private static string Answer(string stop, IEnumerable<JsonObject> blocks) => new JsonObject
    {
        ["id"] = "msg", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-haiku-5-5", ["stop_reason"] = stop, ["stop_sequence"] = null,
        ["content"] = new JsonArray([new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig" }, .. blocks]),
        ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 10, ["cache_read_input_tokens"] = 0, ["cache_creation_input_tokens"] = 0 },
    }.ToJsonString();

    private sealed class Fake(Func<HttpRequestMessage, Task<string>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await answer(request), System.Text.Encoding.UTF8, "application/json") };
    }
}
