using Helm.Ai;
using Helm.Ai.Assistant;

namespace Helm.Tests;

/// <summary>
/// Proxy agents kept ready and picked at once: Auto puts a free agent with a process up before the paid API and decides
/// from what is known (never try-then-fall-through), a picked agent is never swapped, and a spare process per agent
/// lets a new conversation start without waiting.
/// </summary>
public sealed class AgentAutoTests
{
    private static AiJob Job(string conversation = "conv-1", string text = "chào em", string model = "claude-haiku-5-5", string effort = "low") =>
        new(AiFeatureIds.AskHelm, conversation, text, [], [], model, effort);

    private static AgentReadiness Agent(string name, bool ready = true, bool warm = false, bool busy = false, bool holds = false, int? firstMs = null) =>
        new(name, "PC", ready, warm, busy, holds, firstMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

    // ---- AgentPicker ------------------------------------------------------------------------------------------

    [Fact]
    public void Auto_takes_a_free_agent_with_a_process_up_before_a_cold_one_and_the_API()
    {
        var pick = AgentPicker.Choose([Agent("Codex"), Agent("Claude Code", warm: true)], null, apiReady: true);

        Assert.Equal("Claude Code", pick.Chosen?.Agent);
        Assert.False(pick.Api);
    }

    [Fact]
    public void A_cold_agent_still_comes_before_the_paid_API()
    {
        var pick = AgentPicker.Choose([Agent("Codex", ready: false), Agent("Grok")], null, apiReady: true);

        Assert.Equal("Grok", pick.Chosen?.Agent);
    }

    [Fact]
    public void The_API_is_chosen_at_once_when_no_agent_can_take_it()
    {
        var pick = AgentPicker.Choose([Agent("Codex", ready: false), Agent("Claude Code", busy: true, holds: true)], null, apiReady: true);

        Assert.True(pick.Api);
        Assert.Null(pick.Chosen);
        Assert.Null(pick.Problem);
    }

    [Fact]
    public void The_conversations_own_agent_keeps_it_unless_it_is_busy_with_an_earlier_turn()
    {
        Assert.Equal("Codex", AgentPicker.Choose([Agent("Claude Code", warm: true), Agent("Codex", holds: true)], null, true).Chosen?.Agent);
        Assert.Equal("Claude Code",
            AgentPicker.Choose([Agent("Claude Code", warm: true), Agent("Codex", holds: true, busy: true)], null, true).Chosen?.Agent);
    }

    [Fact]
    public void With_no_API_a_busy_agent_takes_the_turn_after_its_earlier_one()
    {
        var pick = AgentPicker.Choose([Agent("Codex", holds: true, busy: true)], null, apiReady: false);

        Assert.Equal("Codex", pick.Chosen?.Agent);
    }

    [Fact]
    public void Nothing_ready_and_no_API_is_said()
    {
        var pick = AgentPicker.Choose([Agent("Codex", ready: false)], null, apiReady: false);

        Assert.Null(pick.Chosen);
        Assert.False(pick.Api);
        Assert.NotNull(pick.Problem);
    }

    [Fact]
    public void A_picked_agent_is_never_swapped_for_another_or_the_API()
    {
        var agents = new[] { Agent("Codex", ready: false), Agent("Claude Code", warm: true) };

        var pick = AgentPicker.Choose(agents, "codex", apiReady: true);

        Assert.Null(pick.Chosen);
        Assert.False(pick.Api);
        Assert.Contains("Codex", pick.Problem);
        Assert.Equal("Codex", AgentPicker.Choose([Agent("Codex", busy: true, holds: true), Agent("Grok", warm: true)], "Codex", true).Chosen?.Agent);
        Assert.Contains("not set up", AgentPicker.Choose(agents, "Gemini", true).Problem);
    }

    [Fact]
    public void Among_agents_equally_ready_the_one_that_answered_sooner_lately_goes_first()
    {
        AgentReadiness[] agents = [Agent("Codex", warm: true), Agent("Grok", warm: true, firstMs: 1900), Agent("Claude Code", warm: true, firstMs: 700)];

        Assert.Equal("Claude Code", AgentPicker.Choose(agents, null, true).Chosen?.Agent);
        // The user's order, when asked to keep it.
        Assert.Equal("Codex", AgentPicker.Choose(agents, null, true, byLatency: false).Chosen?.Agent);
    }

    // ---- the spare --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_conversation_takes_the_spare_already_started_and_another_is_made_ready()
    {
        var agent = new Chats();
        using var local = new LocalAgentRoute("PC", "Claude Code", agent, [], _ => true, () => null) { KeepSpare = true };

        local.Warm(Job("conv-1"));
        await Until(() => agent.Made.Count == 2 && agent.Made.All(c => c.Warms == 1));
        var spare = agent.Made[1];
        Assert.True(local.ReadinessFor("conv-2").Warm);

        var result = await local.RunAsync(Job("conv-2"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Dạ.", result.Text);
        Assert.Equal(1, spare.Turns);
        // The next spare is on its way, like the conversation it replaced.
        await Until(() => agent.Made.Count == 3 && agent.Made[2].Warms == 1);
        Assert.All(agent.Made, c => Assert.False(c.Disposed));
    }

    [Fact]
    public async Task A_spare_made_another_way_is_not_used_and_stops()
    {
        var agent = new Chats();
        using var local = new LocalAgentRoute("PC", "Claude Code", agent, [], _ => true, () => null) { KeepSpare = true };
        local.Warm(Job("conv-1"));
        await Until(() => agent.Made.Count == 2 && agent.Made[1].Warms == 1);
        var spare = agent.Made[1];

        await local.RunAsync(Job("conv-2", effort: "high"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(spare.Disposed);
        Assert.Equal(0, spare.Turns);
        Assert.Equal("high", agent.Made[2].Effort);
    }

    [Fact]
    public async Task No_spare_is_kept_unless_asked_and_one_kept_stops_with_the_route()
    {
        var agent = new Chats();
        using (var plain = new LocalAgentRoute("PC", "Codex", agent, [], _ => true, () => null))
        {
            await plain.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Single(agent.Made);
            Assert.False(plain.ReadinessFor("conv-2").Warm);
        }

        var spared = new Chats();
        var local = new LocalAgentRoute("PC", "Codex", spared, [], _ => true, () => null) { KeepSpare = true };
        await local.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);
        await Until(() => spared.Made.Count == 2 && spared.Made[1].Warms == 1);
        local.Dispose();
        Assert.All(spared.Made, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task An_agent_on_a_turn_is_busy_for_its_conversation_and_its_first_token_is_measured()
    {
        var agent = new Chats { Hold = new TaskCompletionSource() };
        using var local = new LocalAgentRoute("PC", "Grok", agent, [], _ => true, () => null);
        Assert.Null(local.FirstToken);

        var turn = local.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);
        await Until(() => local.ReadinessFor("conv-1").Busy);
        Assert.True(local.ReadinessFor("conv-1").Holds);
        agent.Hold.SetResult();
        await turn;

        var after = local.ReadinessFor("conv-1");
        Assert.False(after.Busy);
        Assert.True(after.Warm);
        Assert.NotNull(local.FirstToken);
    }

    // ---- the router -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_router_decides_at_once_and_warms_the_agent_it_chose()
    {
        var codex = new Chats();
        var claude = new Chats();
        var codexUnavailable = (string?)null;
        using var first = new LocalAgentRoute("PC", "Codex", codex, [], _ => true, () => codexUnavailable);
        using var second = new LocalAgentRoute("PC", "Claude Code", claude, [], _ => true, () => null) { KeepSpare = true };
        var router = new AiRouter([new AgentChain([first, second])]);

        // Claude Code has a process up (its spare): Auto takes it over Codex, which would have to start one.
        await second.RunAsync(Job("conv-0"), TimeSpan.FromSeconds(5), CancellationToken.None);
        await Until(() => claude.Made.Count == 2 && claude.Made[1].Warms == 1);
        var choice = router.Warm(Job("conv-1"), apiReady: true);
        Assert.Equal("Claude Code", choice.Agent);
        Assert.Empty(codex.Made);
        var result = await choice.Route!.RunAsync(choice.For(Job("conv-1")), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Claude Code", result.Agent);
        Assert.Equal(1, claude.Made[1].Turns);

        // Picked by name: that agent, or nothing (not the API) when it cannot.
        codexUnavailable = "reached its plan's limit";
        var picked = router.Choose("conv-2", "Codex", apiReady: true);
        Assert.Null(picked.Route);
        Assert.False(picked.Api);
        Assert.NotNull(picked.Problem);
    }

    [Fact]
    public void The_router_goes_to_the_API_at_once_when_no_agent_is_ready()
    {
        using var only = new LocalAgentRoute("PC", "Codex", new Chats(), [], _ => true, () => "not signed in");
        var router = new AiRouter([new AgentChain([only])]);

        var choice = router.Choose("conv-1", null, apiReady: true);

        Assert.True(choice.Api);
        Assert.Null(choice.Route);
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("It did not happen in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>An agent whose every chat is kept, so a test sees which one was started, used or stopped.</summary>
    private sealed class Chats : IAssistantChatFactory
    {
        private readonly List<Chat> _made = [];

        public AiProvider Provider => AiProviders.Claude;

        /// <summary>Each turn waits for it, when set.</summary>
        public TaskCompletionSource? Hold { get; init; }

        public IReadOnlyList<Chat> Made
        {
            get
            {
                lock (_made) return [.. _made];
            }
        }

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            var chat = new Chat(this, effort);
            lock (_made) _made.Add(chat);
            return chat;
        }

        public sealed class Chat(Chats owner, string effort) : IAssistantChat, IDisposable
        {
            public string Effort => effort;
            public int Warms { get; private set; }
            public int Turns { get; private set; }
            public bool Disposed { get; private set; }

            public Task WarmAsync(CancellationToken ct)
            {
                Warms++;
                return Task.CompletedTask;
            }

            public void AddUser(string text) { }
            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
            public Task<AssistantStep> NextAsync(CancellationToken ct) => NextAsync(null, ct);

            public async Task<AssistantStep> NextAsync(Action<string>? partial, CancellationToken ct)
            {
                if (owner.Hold is { } hold) await hold.Task.WaitAsync(ct);
                Turns++;
                partial?.Invoke("Dạ.");
                return new AssistantStep("Dạ.", [], new AssistantUsage(10, 2, 0, 0), false);
            }

            public void Dispose() => Disposed = true;
        }
    }
}
