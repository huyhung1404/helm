using Helm.Ai;
using Helm.Ai.Assistant;

namespace Helm.Tests;

/// <summary>
/// A proxy agent's answer while it writes it: Claude Code's partial messages read from its stream-json output, a call
/// list never shown, and the text reaching the caller of a PC's agent route.
/// </summary>
public sealed class AgentStreamTests
{
    private static AiJob Job(string text = "chào em", string conversation = "conv-1") =>
        new(AiFeatureIds.AskHelm, conversation, text, [], [], "claude-haiku-5-5", "low");

    // ---- CliPartial -------------------------------------------------------------------------------------------

    [Fact]
    public void Plain_text_is_told_as_it_grows_and_not_again_at_the_end()
    {
        var heard = new List<string>();
        var shown = new CliPartial(heard.Add);

        shown.Show("\nDạ, ");
        shown.Show("\nDạ, em đây.");
        shown.Done(new AssistantStep("Dạ, em đây.", [], new AssistantUsage(0, 0, 0, 0), false));

        Assert.Equal(["Dạ, ", "Dạ, em đây."], heard);
    }

    [Fact]
    public void A_call_list_is_never_shown()
    {
        var heard = new List<string>();
        var shown = new CliPartial(heard.Add);

        shown.Show("""{"calls":[{"name":"wallet""");
        shown.Show("```json\n{\"calls\"");
        shown.Done(new AssistantStep("", [new ToolCall("c1", "wallet_add_transaction", default)], new AssistantUsage(0, 0, 0, 0), false));

        Assert.Empty(heard);
    }

    [Fact]
    public void Text_held_back_or_never_streamed_is_said_whole_at_the_end()
    {
        var heard = new List<string>();
        var shown = new CliPartial(heard.Add);

        shown.Show("{ không phải lời gọi }");
        shown.Done(new AssistantStep("{ không phải lời gọi }", [], new AssistantUsage(0, 0, 0, 0), false));

        Assert.Equal(["{ không phải lời gọi }"], heard);
    }

    [Fact]
    public void Text_said_by_a_step_that_ends_in_calls_is_taken_back()
    {
        var heard = new List<string>();
        var shown = new CliPartial(heard.Add);

        shown.Show("Để em xem");
        shown.Done(new AssistantStep("", [new ToolCall("c1", "wallet_summary", default)], new AssistantUsage(0, 0, 0, 0), false));

        Assert.Equal(["Để em xem", ""], heard);
    }

    // ---- ClaudeCodeStream -------------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_codes_partial_messages_are_told_and_the_result_ends_the_turn()
    {
        var lines = new Queue<string>(
        [
            """{"type":"system","subtype":"init","session_id":"s"}""",
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Chào "}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Master"}},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Chào Master"}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Chào Master","usage":{"input_tokens":12,"output_tokens":3,"cache_read_input_tokens":900,"cache_creation_input_tokens":0}}""",
            """{"type":"result","result":"the next turn's"}""",
        ]);
        var heard = new List<string>();

        var result = await ClaudeCodeStream.ReadTurnAsync(_ => Task.FromResult(lines.TryDequeue(out var l) ? l : null), heard.Add, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Chào Master", result.Answer);
        Assert.False(result.IsError);
        Assert.Equal(new AssistantUsage(12, 3, 900, 0), result.Usage);
        Assert.Equal(["Chào ", "Chào Master"], heard);
        Assert.Single(lines);
    }

    [Fact]
    public async Task A_new_message_starts_the_text_again_and_an_ended_output_is_no_answer()
    {
        var lines = new Queue<string>(
        [
            """{"type":"stream_event","event":{"type":"message_start"},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"one"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"message_start"},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"two"}},"parent_tool_use_id":null}""",
        ]);
        var heard = new List<string>();

        var result = await ClaudeCodeStream.ReadTurnAsync(_ => Task.FromResult(lines.TryDequeue(out var l) ? l : null), heard.Add, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(["one", "two"], heard);
    }

    [Fact]
    public async Task An_error_result_is_marked()
    {
        var lines = new Queue<string>(["""{"type":"result","is_error":true,"result":"Claude AI usage limit reached"}"""]);

        var result = await ClaudeCodeStream.ReadTurnAsync(_ => Task.FromResult(lines.TryDequeue(out var l) ? l : null), null, CancellationToken.None);

        Assert.True(result!.IsError);
        Assert.Contains("limit", result.Answer);
    }

    // ---- the route --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pcs_agent_route_passes_the_answer_so_far_to_its_caller()
    {
        var agent = new Streaming("Dạ, ", "Dạ, em đây.");
        using var local = new LocalAgentRoute("PC", "Claude Code", agent, [], _ => true, () => null);
        IAgentRoute route = new AgentChain([local]);
        var heard = new List<string>();

        var result = await route.RunAsync(Job(), TimeSpan.FromSeconds(5), heard.Add, CancellationToken.None);

        Assert.Equal("Dạ, em đây.", result.Text);
        Assert.Equal(["Dạ, ", "Dạ, em đây."], heard);
    }

    [Fact]
    public async Task A_route_that_cannot_stream_runs_the_job_whole()
    {
        IAgentRoute route = new Whole();
        var heard = new List<string>();

        var result = await route.RunAsync(Job(), TimeSpan.FromSeconds(5), heard.Add, CancellationToken.None);

        Assert.Equal("Dạ.", result.Text);
        Assert.Empty(heard);
    }

    /// <summary>An agent chat that writes its answer in the pieces given (each the text so far), the last one whole.</summary>
    internal sealed class Streaming(params string[] soFar) : IAssistantChatFactory, IAssistantChat
    {
        public AiProvider Provider => AiProviders.Claude;
        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools) => this;
        public void AddUser(string text) { }
        public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        public Task<AssistantStep> NextAsync(CancellationToken ct) => NextAsync(null, ct);

        public Task<AssistantStep> NextAsync(Action<string>? partial, CancellationToken ct)
        {
            foreach (var text in soFar) partial?.Invoke(text);
            return Task.FromResult(new AssistantStep(soFar[^1], [], new AssistantUsage(10, 2, 0, 0), false));
        }
    }

    private sealed class Whole : IAgentRoute
    {
        public string Device => "PC-B";
        public string? Unavailable() => null;
        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct) => Task.FromResult(new AiJobResult("Dạ.", [], [], null));
        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;
        public void End(string conversationId) { }
    }
}
