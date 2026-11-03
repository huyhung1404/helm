using System.Text.Json.Nodes;
using System.Threading.Channels;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;

namespace Helm.Tests;

/// <summary>
/// Grok's agent mode (ACP over stdio): the handshake that gives Grok no file or terminal access, a session refused
/// when it keeps tools of its own, turns with their text and tokens, Grok's own requests answered, and the process
/// started again (with the protocol and a recap) when it died; and what a headless run prints.
/// </summary>
public sealed class GrokAcpTests
{
    private static readonly JsonObject Meta = new() { ["systemPromptOverride"] = "Helm", ["agentProfile"] = new JsonObject { ["name"] = "helm-assistant" } };

    // ---- GrokAcp ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_handshake_offers_no_files_or_terminal_and_passes_the_profile()
    {
        var grok = new FakeGrok();
        var acp = new GrokAcp(grok.Output, grok.Input);

        await acp.StartAsync(@"C:\helm\work", Meta, CancellationToken.None);

        Assert.Equal(["initialize", "session/new"], grok.Sent.Select(m => (string)m["method"]!));
        Assert.All(grok.Sent, m => Assert.Equal("2.0", (string)m["jsonrpc"]!));
        Assert.Equal([1L, 2L], grok.Sent.Select(m => (long)m["id"]!));
        var caps = grok.Sent[0]["params"]!["clientCapabilities"]!;
        Assert.False((bool)caps["fs"]!["readTextFile"]!);
        Assert.False((bool)caps["fs"]!["writeTextFile"]!);
        Assert.False((bool)caps["terminal"]!);
        var open = grok.Sent[1]["params"]!;
        Assert.Equal(@"C:\helm\work", (string)open["cwd"]!);
        Assert.Empty(open["mcpServers"]!.AsArray());
        Assert.Equal("helm-assistant", (string)open["_meta"]!["agentProfile"]!["name"]!);
    }

    [Fact]
    public async Task A_session_that_keeps_tools_of_its_own_is_refused()
    {
        var grok = new FakeGrok { Tools = ["run_terminal_command", "read_file"] };
        var acp = new GrokAcp(grok.Output, grok.Input);

        var refused = await Assert.ThrowsAsync<GrokToolsOfferedException>(() => acp.StartAsync("work", Meta, CancellationToken.None));

        Assert.Contains("run_terminal_command", refused.Message);
    }

    [Fact]
    public async Task A_session_that_does_not_say_which_tools_it_has_is_refused()
    {
        var grok = new FakeGrok { Tools = null };
        var acp = new GrokAcp(grok.Output, grok.Input);

        await Assert.ThrowsAsync<GrokToolsOfferedException>(() => acp.StartAsync("work", Meta, CancellationToken.None));
    }

    [Fact]
    public async Task Tools_listed_before_the_session_is_answered_count_too()
    {
        var grok = new FakeGrok { ListBeforeAnswer = true };
        var acp = new GrokAcp(grok.Output, grok.Input);

        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (id, _) => [Chunk("ok"), Done(id)];

        Assert.Equal("ok", (await acp.TurnAsync("hi", CancellationToken.None)).Answer);
    }

    [Fact]
    public async Task A_turn_joins_the_answer_and_counts_the_cached_tokens_apart()
    {
        var grok = new FakeGrok();
        var acp = new GrokAcp(grok.Output, grok.Input);
        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (id, turn) =>
        [
            Update(new JsonObject { ["sessionUpdate"] = "agent_thought_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "thinking" } }),
            Chunk("Chào "),
            Chunk($"Anh {turn}"),
            Note("_x.ai/queue/changed", new JsonObject { ["sessionId"] = "s-1" }),
            Done(id, input: 1477, cached: 1152, output: 83),
        ];

        var (answer, usage) = await acp.TurnAsync("Chào em", CancellationToken.None);
        Assert.Equal("Chào Anh 1", answer);
        Assert.Equal(new AssistantUsage(325, 83, 1152, 0), usage);
        var prompt = grok.Sent.Last()["params"]!;
        Assert.Equal("s-1", (string)prompt["sessionId"]!);
        Assert.Equal("Chào em", (string)prompt["prompt"]![0]!["text"]!);

        // The session stays: the next turn goes to the same one.
        Assert.Equal("Chào Anh 2", (await acp.TurnAsync("nữa", CancellationToken.None)).Answer);
        Assert.Equal("s-1", (string)grok.Sent.Last()["params"]!["sessionId"]!);
    }

    [Fact]
    public async Task A_permission_is_cancelled_and_a_file_request_turned_down_and_the_turn_goes_on()
    {
        var grok = new FakeGrok();
        var acp = new GrokAcp(grok.Output, grok.Input);
        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (id, _) =>
        [
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 0, ["method"] = "session/request_permission", ["params"] = new JsonObject { ["sessionId"] = "s-1" } },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "f", ["method"] = "fs/read_text_file", ["params"] = new JsonObject { ["path"] = @"C:\secret" } },
            Chunk("ok"),
            Done(id),
        ];

        Assert.Equal("ok", (await acp.TurnAsync("hi", CancellationToken.None)).Answer);

        var permission = grok.Sent.Single(m => m["id"] is JsonValue v && v.TryGetValue<long>(out var n) && n == 0 && m["method"] is null);
        Assert.Equal("cancelled", (string)permission["result"]!["outcome"]!["outcome"]!);
        var file = grok.Sent.Single(m => m["id"] is JsonValue v && v.TryGetValue<string>(out var s) && s == "f");
        Assert.NotNull(file["error"]);
        Assert.Null(file["result"]);
    }

    [Fact]
    public async Task A_failed_turn_says_why()
    {
        var grok = new FakeGrok();
        var acp = new GrokAcp(grok.Output, grok.Input);
        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (id, _) =>
        [
            new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "Internal error", ["data"] = "You have reached your usage limit." },
            },
        ];

        var failed = await Assert.ThrowsAsync<AssistantException>(() => acp.TurnAsync("hi", CancellationToken.None));

        Assert.Contains("usage limit", failed.Message);
    }

    [Fact]
    public async Task A_cancelled_turn_is_not_an_answer()
    {
        var grok = new FakeGrok();
        var acp = new GrokAcp(grok.Output, grok.Input);
        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (id, _) => [Chunk("half"), Done(id, stop: "cancelled")];

        await Assert.ThrowsAsync<AssistantException>(() => acp.TurnAsync("hi", CancellationToken.None));
    }

    [Fact]
    public async Task Grok_stopping_mid_turn_is_an_io_error()
    {
        var grok = new FakeGrok();
        var acp = new GrokAcp(grok.Output, grok.Input);
        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (_, _) => [Chunk("half"), null];

        await Assert.ThrowsAsync<IOException>(() => acp.TurnAsync("hi", CancellationToken.None));
    }

    [Fact]
    public async Task Lines_that_are_not_json_are_skipped()
    {
        var grok = new FakeGrok { Noise = true };
        var acp = new GrokAcp(grok.Output, grok.Input);
        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (id, _) => [Chunk("ok"), Done(id)];

        Assert.Equal("ok", (await acp.TurnAsync("hi", CancellationToken.None)).Answer);
    }

    [Fact]
    public async Task A_photo_goes_as_an_image_block_after_the_words()
    {
        var grok = new FakeGrok();
        var acp = new GrokAcp(grok.Output, grok.Input);
        await acp.StartAsync("work", Meta, CancellationToken.None);
        grok.OnPrompt = (id, _) => [Chunk("Red."), Done(id)];

        Assert.Equal("Red.", (await acp.TurnAsync("What colour?", [new AssistantImage([1, 2, 3], "image/png")], CancellationToken.None)).Answer);

        var prompt = grok.Sent.Last()["params"]!["prompt"]!.AsArray();
        Assert.Equal(2, prompt.Count);
        Assert.Equal("text", (string)prompt[0]!["type"]!);
        Assert.Equal("image", (string)prompt[1]!["type"]!);
        Assert.Equal("image/png", (string)prompt[1]!["mimeType"]!);
        Assert.Equal("AQID", (string)prompt[1]!["data"]!);
        Assert.Equal("s-1", acp.Session);
    }

    // ---- GrokAgentMode ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_warmed_session_still_gets_the_protocol_with_the_first_turn()
    {
        var started = new List<FakeGrok>();
        using var mode = Mode(started);
        var conversation = new CliConversation("Be Helm.", [Tool("wallet_add_transaction")]);

        await mode.WarmAsync(CancellationToken.None);
        Assert.Single(started);
        Assert.Empty(started[0].Prompts());

        conversation.AddUser("phở 45k");
        await mode.TurnAsync(conversation, CancellationToken.None);

        var grok = Assert.Single(started);
        Assert.Contains("wallet_add_transaction", Assert.Single(grok.Prompts()));
        Assert.Equal(["s-1"], mode.Sessions);
    }

    [Fact]
    public async Task One_process_takes_every_turn_and_only_the_first_gets_the_protocol()
    {
        var started = new List<FakeGrok>();
        using var mode = Mode(started);
        var conversation = new CliConversation("Be Helm.", [Tool("wallet_add_transaction")]);

        conversation.AddUser("phở 45k");
        var first = await mode.TurnAsync(conversation, CancellationToken.None);
        conversation.Answered(first!.Value.Answer);
        conversation.AddUser("thêm");
        await mode.TurnAsync(conversation, CancellationToken.None);

        var grok = Assert.Single(started);
        var prompts = grok.Prompts();
        Assert.Contains("wallet_add_transaction", prompts[0]);
        Assert.Equal("thêm", prompts[1]);
    }

    [Fact]
    public async Task A_process_that_died_between_turns_is_started_again_with_the_protocol_and_a_recap()
    {
        var started = new List<FakeGrok>();
        using var mode = Mode(started);
        var conversation = new CliConversation("Be Helm.", [Tool("wallet_add_transaction")]);
        conversation.AddUser("phở 45k");
        conversation.Answered((await mode.TurnAsync(conversation, CancellationToken.None))!.Value.Answer);

        started[0].Exited = true;
        conversation.AddUser("thêm");
        var again = await mode.TurnAsync(conversation, CancellationToken.None);

        Assert.NotNull(again);
        Assert.Equal(2, started.Count);
        Assert.True(started[0].Killed);
        var prompt = Assert.Single(started[1].Prompts());
        Assert.Contains("wallet_add_transaction", prompt);
        Assert.Contains("phở 45k", prompt);
        Assert.EndsWith("thêm", prompt);
    }

    [Fact]
    public async Task A_process_that_dies_mid_turn_fails_the_turn_and_the_next_one_starts_over()
    {
        var started = new List<FakeGrok>();
        using var mode = Mode(started, first => first.OnPrompt = (_, _) => [Chunk("half"), null]);
        var conversation = new CliConversation("Be Helm.", []);
        conversation.AddUser("hi");

        await Assert.ThrowsAsync<AssistantException>(() => mode.TurnAsync(conversation, CancellationToken.None));
        Assert.True(started[0].Killed);

        var again = await mode.TurnAsync(conversation, CancellationToken.None);
        Assert.Equal("answer 1", again!.Value.Answer);
        Assert.Contains("Be Helm.", Assert.Single(started[1].Prompts()));
    }

    [Fact]
    public async Task A_session_with_tools_is_refused_for_good_and_no_prompt_is_sent()
    {
        var started = new List<FakeGrok>();
        using var mode = Mode(started, first => first.Tools = ["read_file"]);
        var conversation = new CliConversation("Be Helm.", []);
        conversation.AddUser("hi");

        Assert.Null(await mode.TurnAsync(conversation, CancellationToken.None));
        Assert.Contains("read_file", mode.Refused);
        Assert.Empty(started[0].Prompts());
        Assert.True(started[0].Killed);

        Assert.Null(await mode.TurnAsync(conversation, CancellationToken.None));
        Assert.Single(started);
        Assert.True(conversation.HasPending);
    }

    [Fact]
    public async Task A_limit_at_the_start_is_the_agents_error_not_a_refusal()
    {
        var started = new List<FakeGrok>();
        var failures = new List<string>();
        using var mode = Mode(started, first => first.RefuseSessions = "You have reached your usage limit.", failures);
        var conversation = new CliConversation("Be Helm.", []);
        conversation.AddUser("hi");

        await Assert.ThrowsAsync<AssistantException>(() => mode.TurnAsync(conversation, CancellationToken.None));

        Assert.Null(mode.Refused);
        Assert.Contains(failures, f => f.Contains("usage limit"));
    }

    [Fact]
    public async Task A_session_that_never_opens_is_refused_after_the_wait()
    {
        var started = new List<FakeGrok>();
        using var mode = new GrokAgentMode(() =>
        {
            var grok = new FakeGrok { Silent = true };
            started.Add(grok);
            return grok.Process();
        }, "work", Meta, _ => false) { StartTimeout = TimeSpan.FromMilliseconds(100) };
        var conversation = new CliConversation("Be Helm.", []);
        conversation.AddUser("hi");

        Assert.Null(await mode.TurnAsync(conversation, CancellationToken.None));
        Assert.Contains("in time", mode.Refused);
        Assert.True(started[0].Killed);
    }

    // ---- GrokHeadlessAnswer -----------------------------------------------------------------------------------

    [Fact]
    public void A_headless_answer_keeps_its_input_tokens_apart_from_the_cached_ones()
    {
        // Grok 1.0.70: input_tokens + cache_read_input_tokens + output_tokens = total_tokens.
        var (answer, error, usage) = GrokHeadlessAnswer.Read("""
            {"text":"Chào anh.","stopReason":"stop","usage":{"input_tokens":615,"cache_read_input_tokens":1152,"cache_creation_input_tokens":0,"output_tokens":90,"reasoning_tokens":78,"total_tokens":1857}}
            """);

        Assert.Equal("Chào anh.", answer);
        Assert.Null(error);
        Assert.Equal(new AssistantUsage(615, 90, 1152, 0), usage);
    }

    [Fact]
    public void A_headless_error_is_not_an_answer()
    {
        var (answer, error, _) = GrokHeadlessAnswer.Read("""{"is_error":true,"error":"not logged in"}""");

        Assert.Null(answer);
        Assert.Equal("not logged in", error);
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private static GrokAgentMode Mode(List<FakeGrok> started, Action<FakeGrok>? first = null, List<string>? failures = null) =>
        new(() =>
        {
            var grok = new FakeGrok { OnPrompt = (id, turn) => [Chunk($"answer {turn}"), Done(id)] };
            if (started.Count == 0) first?.Invoke(grok);
            started.Add(grok);
            return grok.Process();
        }, "work", Meta, error =>
        {
            failures?.Add(error);
            return error.Contains("limit", StringComparison.OrdinalIgnoreCase);
        });

    private static AssistantTool Tool(string name) =>
        new(new McpTool(name, $"The {name} tool.", McpTool.NoArguments(), (_, _) => Task.FromResult<object?>("ok")), AssistantToolMode.Auto);

    private static JsonObject Note(string method, JsonObject parameters) => new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };

    private static JsonObject Update(JsonObject update) => Note("session/update", new JsonObject { ["sessionId"] = "s-1", ["update"] = update });

    private static JsonObject Chunk(string text) =>
        Update(new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } });

    private static JsonObject Done(long id, long input = 10, long cached = 0, long output = 5, string stop = "end_turn") => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = new JsonObject
        {
            ["stopReason"] = stop,
            ["_meta"] = new JsonObject
            {
                ["usage"] = new JsonObject
                {
                    ["inputTokens"] = input, ["outputTokens"] = output, ["totalTokens"] = input + output, ["cachedReadTokens"] = cached,
                    ["cacheCreationTokens"] = 0, ["modelCalls"] = 1,
                },
            },
        },
    };

    /// <summary>
    /// A <c>grok agent stdio</c> on two in-memory streams: answers the handshake (listing <see cref="Tools"/> for the
    /// session, as Grok does), and each session/prompt with what <see cref="OnPrompt"/> scripts (a null line ends its
    /// output, as a process that died).
    /// </summary>
    private sealed class FakeGrok
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();
        private int _turns;

        public FakeGrok()
        {
            Output = new ChannelReader(_lines.Reader);
            Input = new LineWriter(Answer);
        }

        public TextReader Output { get; }
        public TextWriter Input { get; }
        public List<JsonObject> Sent { get; } = [];
        public string[]? Tools { get; set; } = [];
        public bool ListBeforeAnswer { get; init; }
        public bool Noise { get; init; }
        public bool Silent { get; init; }
        public string? RefuseSessions { get; set; }
        public bool Exited { get; set; }
        public bool Killed { get; private set; }
        public Func<long, int, IEnumerable<JsonObject?>> OnPrompt { get; set; } = (_, _) => [];

        public GrokAgentProcess Process() => new(Output, Input, () => Exited, () =>
        {
            Killed = true;
            _lines.Writer.TryComplete();
        });

        public List<string> Prompts() =>
            [.. Sent.Where(m => (string?)m["method"] == "session/prompt").Select(m => (string)m["params"]!["prompt"]![0]!["text"]!)];

        private void Answer(string line)
        {
            var message = (JsonObject)JsonNode.Parse(line)!;
            Sent.Add(message);
            if (Silent || message["method"] is null) return;
            var id = (long)message["id"]!;
            switch ((string?)message["method"])
            {
                case "initialize":
                    if (Noise) _lines.Writer.TryWrite("Grok 1.0.70 starting…");
                    Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["protocolVersion"] = 1 } });
                    break;
                case "session/new" when RefuseSessions is { } why:
                    Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = why } });
                    break;
                case "session/new":
                    Write(Note("_x.ai/session/setup", new JsonObject { ["method"] = "session/new", ["phase"] = "agent_build" }));
                    if (ListBeforeAnswer) Write(Listed());
                    Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["sessionId"] = "s-1" } });
                    if (Noise) _lines.Writer.TryWrite("");
                    if (!ListBeforeAnswer) Write(Listed());
                    break;
                case "session/prompt":
                    foreach (var reply in OnPrompt(id, ++_turns)) Write(reply);
                    break;
            }
        }

        private JsonObject Listed()
        {
            var update = new JsonObject { ["sessionUpdate"] = "available_commands_update", ["availableCommands"] = new JsonArray() };
            if (Tools is { } tools) update["_meta"] = new JsonObject { ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)t)]) };
            return Update(update);
        }

        private void Write(JsonObject? message)
        {
            if (message is null) _lines.Writer.TryComplete();
            else _lines.Writer.TryWrite(message.ToJsonString());
        }

        private sealed class ChannelReader(ChannelReader<string?> lines) : TextReader
        {
            public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
            {
                try
                {
                    return await lines.ReadAsync(ct);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }
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
