using System.Text.Json.Nodes;
using System.Threading.Channels;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;

namespace Helm.Tests;

/// <summary>
/// The agent CLIs' chats (Claude Code, Codex, Grok): what waits is kept until a turn went through, a new session gets
/// the protocol and a recap, and the Codex app server's protocol (handshake, turn, its own requests turned down, usage).
/// </summary>
public sealed class CliAgentChatTests
{
    private static readonly AssistantTool Add = Tool("wallet_add_transaction");
    private static readonly AssistantTool Summary = Tool("wallet_summary");

    // ---- CliConversation --------------------------------------------------------------------------------------

    [Fact]
    public void The_first_message_has_the_protocol_then_the_words()
    {
        var chat = new CliConversation("Be Helm.", [Add]);
        chat.AddUser("phở 45k");

        var message = chat.Message(newSession: true);

        Assert.StartsWith("Be Helm.", message);
        Assert.Contains("wallet_add_transaction", message);
        Assert.EndsWith("\n\nphở 45k", message);
        Assert.DoesNotContain("restarted", message);
    }

    [Fact]
    public void A_failed_turn_keeps_the_words_and_the_tool_results_for_the_next()
    {
        var chat = new CliConversation("Be Helm.", [Add]);
        chat.AddUser("phở 45k");
        chat.Answered("""{"calls":[{"name":"wallet_add_transaction","input":{}}]}""");
        chat.AddResults([new ToolOutcome("c1", "saved #42", false)]);
        _ = chat.Message(newSession: false); // the turn fails: no Answered

        chat.AddUser("và cà phê 20k");
        var retry = chat.Message(newSession: false);

        Assert.Contains("saved #42", retry);
        Assert.Contains("và cà phê 20k", retry);
        Assert.True(retry.IndexOf("saved #42", StringComparison.Ordinal) < retry.IndexOf("cà phê", StringComparison.Ordinal));
        Assert.DoesNotContain("Be Helm.", retry);
    }

    [Fact]
    public void An_answered_turn_leaves_nothing_waiting()
    {
        var chat = new CliConversation("Be Helm.", [Add]);
        chat.AddUser("phở 45k");
        _ = chat.Message(newSession: true);
        chat.Answered("Đã ghi.");

        Assert.False(chat.HasPending);
        chat.AddUser("cảm ơn");
        Assert.Equal("cảm ơn", chat.Message(newSession: false));
    }

    [Fact]
    public void Tools_added_later_are_told_to_the_session_once()
    {
        var chat = new CliConversation("Be Helm.", [Add]);
        chat.AddUser("phở 45k");
        chat.Answered("Đã ghi.");
        chat.AddTools([Summary]);
        chat.AddUser("tháng này tiêu bao nhiêu?");

        var message = chat.Message(newSession: false);
        Assert.StartsWith("More tools you may call from now on:", message);
        Assert.Contains("wallet_summary", message);
        chat.Answered("2 triệu.");

        chat.AddUser("còn tuần này?");
        Assert.Equal("còn tuần này?", chat.Message(newSession: false));
    }

    [Fact]
    public void A_new_session_mid_conversation_gets_every_tool_and_a_recap()
    {
        var chat = new CliConversation("Be Helm.", [Add]);
        chat.AddUser("phở 45k");
        chat.Answered("Đã ghi phở 45.000đ.");
        chat.AddTools([Summary]);
        chat.AddUser("tháng này tiêu bao nhiêu?");

        // The process died (or the idle reaper stopped it): the new one knows nothing.
        var message = chat.Message(newSession: true);

        Assert.StartsWith("Be Helm.", message);
        Assert.Contains("- wallet_add_transaction:", message);
        Assert.Contains("- wallet_summary:", message);
        Assert.DoesNotContain("More tools you may call", message);
        Assert.Contains("Helm restarted you", message);
        Assert.Contains("phở 45k", message);
        Assert.Contains("You: Đã ghi phở 45.000đ.", message);
        Assert.EndsWith("tháng này tiêu bao nhiêu?", message);
    }

    [Fact]
    public void A_recap_keeps_the_latest_and_cuts_each_message_short()
    {
        var chat = new CliConversation("Be Helm.", []);
        for (var i = 0; i < 40; i++)
        {
            chat.AddUser($"question {i} " + new string('x', 2000));
            chat.Answered($"answer {i}");
        }
        chat.AddUser("last");

        var recap = chat.Message(newSession: true);

        Assert.Contains("answer 39", recap);
        Assert.DoesNotContain("question 0 ", recap);
        Assert.DoesNotContain(new string('x', CliConversation.RecapEach + 1), recap);
        Assert.True(recap.Length < CliConversation.RecapAll + 2 * CliConversation.RecapEach + 1000);
    }

    // ---- CodexAppServer ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_app_server_opens_a_read_only_thread_that_asks_for_nothing()
    {
        var codex = new FakeCodex();
        var server = new CodexAppServer(codex.Output, codex.Input);

        await server.StartAsync(@"C:\helm\work", "0.79.0", CancellationToken.None);

        Assert.Equal(["initialize", "initialized", "thread/start"], codex.Sent.Select(m => (string)m["method"]!));
        var start = codex.Sent[2]["params"]!;
        Assert.Equal(@"C:\helm\work", (string)start["cwd"]!);
        Assert.Equal("read-only", (string)start["sandbox"]!);
        Assert.Equal("never", (string)start["approvalPolicy"]!);
        Assert.True((bool)start["ephemeral"]!);
        Assert.Null(codex.Sent[1]["id"]);
    }

    [Fact]
    public async Task A_turn_returns_the_last_message_and_the_tokens_of_that_turn()
    {
        var codex = new FakeCodex();
        var server = new CodexAppServer(codex.Output, codex.Input);
        await server.StartAsync("work", "1", CancellationToken.None);

        codex.OnTurn = (id, turn) =>
        [
            Reply(id, new JsonObject { ["turn"] = new JsonObject { ["id"] = "t1", ["items"] = new JsonArray(), ["status"] = "inProgress" } }),
            Note("item/completed", new JsonObject { ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = "r" } }),
            Note("item/completed", new JsonObject { ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = "m", ["text"] = $"answer {turn}" } }),
            Note("thread/tokenUsage/updated", Usage(100 * turn, 40 * turn, 5 * turn, 3 * turn)),
            Note("turn/completed", new JsonObject { ["turn"] = new JsonObject { ["id"] = "t1", ["status"] = "completed" } }),
        ];

        var (first, used) = await server.TurnAsync("phở 45k", CancellationToken.None);
        Assert.Equal("answer 1", first);
        Assert.Equal(new AssistantUsage(60, 8, 40, 0), used);
        var sent = codex.Sent.Last(m => (string?)m["method"] == "turn/start")["params"]!;
        Assert.Equal("thread-1", (string)sent["threadId"]!);
        Assert.Equal("phở 45k", (string)sent["input"]![0]!["text"]!);

        // The totals are the thread's: a turn counts only what it added.
        var (second, more) = await server.TurnAsync("thêm", CancellationToken.None);
        Assert.Equal("answer 2", second);
        Assert.Equal(new AssistantUsage(60, 8, 40, 0), more);
    }

    [Fact]
    public async Task A_turn_with_a_photo_sends_it_after_the_words_as_an_image_input()
    {
        var codex = new FakeCodex();
        var server = new CodexAppServer(codex.Output, codex.Input);
        await server.StartAsync("work", "1", CancellationToken.None);
        codex.OnTurn = (id, _) =>
        [
            Reply(id, new JsonObject()),
            Note("item/completed", new JsonObject { ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = "m", ["text"] = "ok" } }),
            Note("turn/completed", new JsonObject { ["turn"] = new JsonObject { ["id"] = "t", ["status"] = "completed" } }),
        ];

        var (answer, _) = await server.TurnAsync("Read the photo.", [new AssistantImage([1, 2, 3])], CancellationToken.None);

        Assert.Equal("ok", answer);
        var input = codex.Sent.Last(m => (string?)m["method"] == "turn/start")["params"]!["input"]!;
        Assert.Equal("Read the photo.", (string)input[0]!["text"]!);
        Assert.Equal("image", (string)input[1]!["type"]!);
        Assert.Equal("data:image/jpeg;base64,AQID", (string)input[1]!["url"]!);
    }

    [Fact]
    public async Task Codexs_own_requests_are_turned_down_and_the_turn_goes_on()
    {
        var codex = new FakeCodex();
        var server = new CodexAppServer(codex.Output, codex.Input);
        await server.StartAsync("work", "1", CancellationToken.None);
        codex.OnTurn = (id, _) =>
        [
            new JsonObject { ["id"] = 77, ["method"] = "item/commandExecution/requestApproval", ["params"] = new JsonObject() },
            Reply(id, new JsonObject()),
            Note("item/completed", new JsonObject { ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = "m", ["text"] = "ok" } }),
            Note("turn/completed", new JsonObject { ["turn"] = new JsonObject { ["id"] = "t", ["status"] = "completed" } }),
        ];

        var (answer, _) = await server.TurnAsync("hi", CancellationToken.None);

        Assert.Equal("ok", answer);
        var refusal = codex.Sent.Single(m => m["id"] is JsonValue v && v.GetValue<long>() == 77);
        Assert.Null(refusal["method"]);
        Assert.NotNull(refusal["error"]);
    }

    [Fact]
    public async Task A_failed_turn_says_why()
    {
        var codex = new FakeCodex();
        var server = new CodexAppServer(codex.Output, codex.Input);
        await server.StartAsync("work", "1", CancellationToken.None);
        codex.OnTurn = (id, _) =>
        [
            Reply(id, new JsonObject()),
            Note("error", new JsonObject { ["error"] = new JsonObject { ["message"] = "reconnecting" }, ["willRetry"] = true }),
            Note("turn/completed", new JsonObject
            {
                ["turn"] = new JsonObject { ["id"] = "t", ["status"] = "failed", ["error"] = new JsonObject { ["message"] = "You've hit your usage limit." } },
            }),
        ];

        var failed = await Assert.ThrowsAsync<AssistantException>(() => server.TurnAsync("hi", CancellationToken.None));

        Assert.Contains("usage limit", failed.Message);
    }

    [Fact]
    public async Task A_server_that_stops_mid_turn_is_not_an_answer()
    {
        var codex = new FakeCodex();
        var server = new CodexAppServer(codex.Output, codex.Input);
        await server.StartAsync("work", "1", CancellationToken.None);
        codex.OnTurn = (id, _) => [Reply(id, new JsonObject()), null];

        await Assert.ThrowsAsync<IOException>(() => server.TurnAsync("hi", CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_handshake_is_an_assistant_error()
    {
        var codex = new FakeCodex { RefuseThreads = true };
        var server = new CodexAppServer(codex.Output, codex.Input);

        await Assert.ThrowsAsync<AssistantException>(() => server.StartAsync("work", "1", CancellationToken.None));
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private static AssistantTool Tool(string name) =>
        new(new McpTool(name, $"The {name} tool.", McpTool.NoArguments(), (_, _) => Task.FromResult<object?>("ok")), AssistantToolMode.Auto);

    private static JsonObject Reply(long id, JsonNode result) => new() { ["id"] = id, ["result"] = result };

    private static JsonObject Note(string method, JsonObject parameters) => new() { ["method"] = method, ["params"] = parameters };

    private static JsonObject Usage(long input, long cached, long output, long reasoning) => new()
    {
        ["threadId"] = "thread-1",
        ["turnId"] = "t1",
        ["tokenUsage"] = new JsonObject
        {
            ["total"] = new JsonObject
            {
                ["inputTokens"] = input, ["cachedInputTokens"] = cached, ["outputTokens"] = output,
                ["reasoningOutputTokens"] = reasoning, ["totalTokens"] = input + output + reasoning,
            },
            ["last"] = new JsonObject(),
        },
    };

    /// <summary>
    /// A codex app-server on two in-memory streams: answers the handshake, and each turn/start with what
    /// <see cref="OnTurn"/> scripts (a null line ends its output, as a process that died).
    /// </summary>
    private sealed class FakeCodex
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();
        private int _turns;

        public FakeCodex()
        {
            Output = new ChannelReader(_lines.Reader);
            Input = new LineWriter(Answer);
        }

        public TextReader Output { get; }
        public TextWriter Input { get; }
        public List<JsonObject> Sent { get; } = [];
        public bool RefuseThreads { get; init; }
        public Func<long, int, IEnumerable<JsonObject?>> OnTurn { get; set; } = (_, _) => [];

        private void Answer(string line)
        {
            var message = (JsonObject)JsonNode.Parse(line)!;
            Sent.Add(message);
            var id = message["id"] is JsonValue v && message["method"] is not null ? v.GetValue<long>() : 0;
            switch ((string?)message["method"])
            {
                case "initialize":
                    Write(Reply(id, new JsonObject { ["userAgent"] = "codex", ["codexHome"] = "home" }));
                    Write(Note("remoteControl/status/changed", new JsonObject { ["status"] = "disabled" }));
                    break;
                case "thread/start" when RefuseThreads:
                    Write(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "no" } });
                    break;
                case "thread/start":
                    Write(Note("thread/started", new JsonObject { ["thread"] = new JsonObject { ["id"] = "thread-1" } }));
                    Write(Reply(id, new JsonObject { ["thread"] = new JsonObject { ["id"] = "thread-1" } }));
                    break;
                case "turn/start":
                    foreach (var reply in OnTurn(id, ++_turns)) Write(reply);
                    break;
            }
        }

        private void Write(JsonObject? message) => _lines.Writer.TryWrite(message?.ToJsonString());

        private sealed class ChannelReader(ChannelReader<string?> lines) : TextReader
        {
            public override async ValueTask<string?> ReadLineAsync(CancellationToken ct) => await lines.ReadAsync(ct);
        }

        private sealed class LineWriter(Action<string> sent) : StringWriter
        {
            public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken ct = default)
            {
                sent(buffer.ToString());
                return Task.CompletedTask;
            }

            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        }
    }
}
