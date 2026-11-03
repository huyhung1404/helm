using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Ai.Vps;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>
/// The VPS worker: hello on connect, claim and result, decline when no agent is ready or it is full, errors, text-only
/// jobs with the service's own system prompt, reconnecting after a drop, and the address and token kept on this PC.
/// </summary>
public sealed class VpsWorkerTests : IDisposable
{
    private static readonly Uri Url = new("wss://vps.example/ai-worker");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly Sockets _sockets = new();
    private readonly Route _route = new();
    private readonly List<string> _ready = ["Codex", "Grok"];
    private readonly VpsWorker _worker;
    private readonly TempDir _dir = new();

    public VpsWorkerTests() => _worker = new VpsWorker(_sockets.Next, _route, () => [.. _ready], "PC-HUNG", "0.79.0") { FirstRetry = TimeSpan.FromMilliseconds(20) };

    public void Dispose()
    {
        _worker.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task It_says_hello_with_the_ready_agents_and_the_token_as_bearer()
    {
        _worker.Start(Url, "secret-token");

        var socket = await _sockets.ConnectedAsync(0);
        var hello = await socket.NextAsync();

        Assert.Equal("hello", hello.Text("type"));
        Assert.Equal("PC-HUNG", hello.Text("device"));
        Assert.Equal(["Codex", "Grok"], hello.GetProperty("agents").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("0.79.0", hello.Text("version"));
        Assert.Equal("secret-token", socket.Token);
        await Until(() => _worker.Status.Connected);
        Assert.Equal("vps.example", _worker.Status.Host);
    }

    [Fact]
    public async Task A_change_of_ready_agents_is_sent_once()
    {
        _worker.Start(Url, "t");
        var socket = await _sockets.ConnectedAsync(0);
        await socket.NextAsync();
        await Until(() => _worker.Status.Connected);

        _worker.SendStatus();
        _ready.Clear();
        _worker.SendStatus();
        _worker.SendStatus();

        var status = await socket.NextAsync();
        Assert.Equal("status", status.Text("type"));
        Assert.Empty(status.GetProperty("agents").EnumerateArray());
        await Task.Delay(100);
        Assert.Equal(0, socket.Unread);
    }

    [Fact]
    public async Task A_job_is_claimed_run_text_only_and_answered()
    {
        _route.Answer = job => new AiJobResult($"Đáp: {job.Text}", [], [], 0.0012m) { Agent = "Codex" };
        var socket = await ConnectAsync();

        socket.Receive("""{"type":"job","id":"j1","feature":"blog.summary","system":"You summarise posts.","prompt":"Tóm tắt bài này","model":"claude-haiku-5-5","effort":"low"}""");

        Assert.Equal("claim", (await socket.NextAsync()).Text("type"));
        var result = await socket.NextAsync();
        Assert.Equal("result", result.Text("type"));
        Assert.Equal("j1", result.Text("id"));
        Assert.Equal("Đáp: Tóm tắt bài này", result.Text("text"));
        Assert.Equal("Codex", result.Text("agent"));
        Assert.Equal(0.0012m, result.GetProperty("savedUsd").GetDecimal());

        var job = Assert.Single(_route.Jobs);
        Assert.Equal("j1", job.ConversationId);
        Assert.Equal("blog.summary", job.Feature);
        Assert.Equal("You summarise posts.", job.System);
        Assert.Empty(job.Tools);
        Assert.Empty(job.PhoneTools);
        await Until(() => _route.Ended.Contains("j1"));
        await Until(() => _worker.Status.JobsToday == 1);
    }

    [Fact]
    public async Task A_job_without_a_system_prompt_gets_a_neutral_one()
    {
        var socket = await ConnectAsync();

        socket.Receive("""{"type":"job","id":"j1","feature":"x","system":null,"prompt":"hi","model":"m","effort":"low"}""");
        await socket.NextAsync();
        await socket.NextAsync();

        Assert.Equal(VpsWorker.NeutralSystem, Assert.Single(_route.Jobs).System);
    }

    [Fact]
    public async Task With_no_agent_ready_a_job_is_declined_and_never_run()
    {
        _route.Why = "Codex: not signed in";
        var socket = await ConnectAsync();

        socket.Receive("""{"type":"job","id":"j1","feature":"x","system":null,"prompt":"hi","model":"m","effort":"low"}""");

        var decline = await socket.NextAsync();
        Assert.Equal("decline", decline.Text("type"));
        Assert.Equal("j1", decline.Text("id"));
        Assert.Equal("Codex: not signed in", decline.Text("reason"));
        Assert.Empty(_route.Jobs);
    }

    [Fact]
    public async Task A_job_beyond_the_limit_is_declined()
    {
        var release = new TaskCompletionSource();
        _route.Hold = release.Task;
        var socket = await ConnectAsync();

        for (var i = 1; i <= VpsWorker.MaxJobs + 1; i++)
            socket.Receive($$"""{"type":"job","id":"j{{i}}","feature":"x","system":null,"prompt":"hi","model":"m","effort":"low"}""");

        var frames = new[] { await socket.NextAsync(), await socket.NextAsync(), await socket.NextAsync() };
        Assert.Equal(VpsWorker.MaxJobs, frames.Count(f => f.Text("type") == "claim"));
        Assert.Equal("j3", Assert.Single(frames, f => f.Text("type") == "decline").Text("id"));
        release.SetResult();
        Assert.Equal("result", (await socket.NextAsync()).Text("type"));
        Assert.Equal("result", (await socket.NextAsync()).Text("type"));
    }

    [Fact]
    public async Task An_agent_that_fails_sends_an_error()
    {
        _route.Answer = _ => throw new AiRouteException("PC-HUNG did not answer within 120 s.");
        var socket = await ConnectAsync();

        socket.Receive("""{"type":"job","id":"j1","feature":"x","system":null,"prompt":"hi","model":"m","effort":"low"}""");

        Assert.Equal("claim", (await socket.NextAsync()).Text("type"));
        var error = await socket.NextAsync();
        Assert.Equal("error", error.Text("type"));
        Assert.Equal("j1", error.Text("id"));
        Assert.Equal("PC-HUNG did not answer within 120 s.", error.Text("message"));
        await Until(() => _route.Ended.Contains("j1"));
    }

    [Fact]
    public async Task Frames_that_are_not_jobs_are_left()
    {
        var socket = await ConnectAsync();

        socket.Receive("not json");
        socket.Receive("""{"type":"ping"}""");
        socket.Receive("""{"type":"job","prompt":"no id"}""");
        await Task.Delay(100);

        Assert.Equal(0, socket.Unread);
        Assert.True(_worker.Status.Connected);
    }

    [Fact]
    public async Task After_a_drop_it_connects_again_and_says_hello_again()
    {
        var first = await ConnectAsync();

        first.Close();
        var second = await _sockets.ConnectedAsync(1);

        Assert.Equal("hello", (await second.NextAsync()).Text("type"));
        await Until(() => _worker.Status.Connected);
    }

    [Fact]
    public async Task A_refused_connection_is_said_and_tried_again()
    {
        _sockets.Refuse = 3;
        _worker.Start(Url, "wrong");

        await Until(() => _worker.Status.Problem == "the VPS did not accept the worker token");
        var later = await _sockets.ConnectedAsync(3);
        Assert.Equal("hello", (await later.NextAsync()).Text("type"));
    }

    [Fact]
    public async Task Stop_closes_the_connection_and_says_why()
    {
        var socket = await ConnectAsync();

        _worker.Stop("turned off");

        await Until(() => socket.Disposed);
        Assert.False(_worker.Status.Connected);
        Assert.Equal("turned off", _worker.Status.Problem);
        await Task.Delay(100);
        Assert.Single(_sockets.Made);
    }

    [Fact]
    public async Task On_a_real_agent_the_job_has_its_system_prompt_and_no_tool()
    {
        var agent = new Capture();
        using var local = new LocalAgentRoute("PC-HUNG", "Codex", agent, [new AnyTool()], _ => true, () => null);
        var job = new AiJob("blog.summary", "j1", "hi", [], [], "claude-haiku-5-5", "low") { System = "You summarise posts." };

        var result = await local.RunAsync(job, Wait, CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Equal("You summarise posts.", agent.System);
        Assert.Empty(agent.Tools);
        // Told nothing about calling tools it does not have.
        Assert.DoesNotContain("\"calls\"", CliAgentProtocol.Preamble(agent.System!, agent.Tools));
        // Ask Helm's turns keep its instructions.
        await local.RunAsync(new AiJob(AiFeatureIds.AskHelm, "c2", "hi", [], [], "m", "low"), Wait, CancellationToken.None);
        Assert.Equal(HelmAssistant.Instructions, agent.System);
    }

    [Theory]
    [InlineData("wss://vps.example/ai-worker", true)]
    [InlineData("ws://127.0.0.1:8787/ai-worker", true)]
    [InlineData("ws://vps.example/ai-worker", false)]
    [InlineData("https://vps.example/ai-worker", false)]
    [InlineData("vps.example", false)]
    public void Only_wss_or_ws_to_this_PC_is_an_address(string text, bool ok)
    {
        Assert.Equal(ok, VpsUrls.Check(text, out var problem) is not null);
        Assert.Equal(ok, problem is null);
    }

    [Fact]
    public void Address_and_token_stay_on_this_PC_and_the_token_is_encrypted()
    {
        var paths = new HelmPaths(_dir.Path);
        using (var settings = new SettingsStoreFactory(paths))
        {
            var control = new VpsWorkerControl(settings.Get<VpsWorkerSettings>(VpsWorkerSettings.StoreId), new VpsWorkerToken(paths, new DpapiSecretProtector()), _worker);
            Assert.NotNull(control.SaveUrl("http://vps.example"));
            Assert.Null(control.SaveUrl("wss://vps.example/ai-worker"));
            control.SetEnabled(true);
            control.SaveToken("  tok-123456  ");
            settings.FlushAll();
        }
        var bytes = Directory.GetFiles(_dir.Path, "*.*", SearchOption.AllDirectories).SelectMany(File.ReadAllBytes).ToArray();
        Assert.DoesNotContain("tok-123456", Encoding.UTF8.GetString(bytes));

        using var again = new SettingsStoreFactory(paths);
        var saved = again.Get<VpsWorkerSettings>(VpsWorkerSettings.StoreId).Current;
        Assert.Equal("wss://vps.example/ai-worker", saved.Url);
        Assert.True(saved.Enabled);
        var token = new VpsWorkerToken(paths, new DpapiSecretProtector());
        Assert.Equal("tok-123456", token.Get());
        token.Delete();
        Assert.False(token.Has);
        Assert.Null(token.Get());
    }

    [Fact]
    public async Task The_settings_start_and_stop_the_worker()
    {
        var paths = new HelmPaths(_dir.Path);
        using var settings = new SettingsStoreFactory(paths);
        var control = new VpsWorkerControl(settings.Get<VpsWorkerSettings>(VpsWorkerSettings.StoreId), new VpsWorkerToken(paths, new PlainSecretProtector()), _worker);

        control.Apply();
        Assert.Equal("turned off", _worker.Status.Problem);
        control.SetEnabled(true);
        Assert.Equal("add your VPS's address", _worker.Status.Problem);
        control.SaveUrl("wss://vps.example/ai-worker");
        Assert.Equal("save the worker token", _worker.Status.Problem);
        control.SaveToken("tok");

        var socket = await _sockets.ConnectedAsync(0);
        Assert.Equal("tok", socket.Token);
        control.SetEnabled(false);
        await Until(() => socket.Disposed);
        Assert.Equal("turned off", _worker.Status.Problem);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task<FakeSocket> ConnectAsync()
    {
        _worker.Start(Url, "t");
        var socket = await _sockets.ConnectedAsync(0);
        Assert.Equal("hello", (await socket.NextAsync()).Text("type"));
        await Until(() => _worker.Status.Connected);
        return socket;
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + Wait;
        while (!done())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Timed out.");
            await Task.Delay(10);
        }
    }

    private sealed class Sockets
    {
        public List<FakeSocket> Made { get; } = [];

        /// <summary>How many first connections the VPS refuses.</summary>
        public int Refuse { get; set; }

        public IVpsSocket Next()
        {
            lock (Made)
            {
                var socket = new FakeSocket(Made.Count < Refuse);
                Made.Add(socket);
                return socket;
            }
        }

        public async Task<FakeSocket> ConnectedAsync(int index)
        {
            await Until(() =>
            {
                lock (Made) return Made.Count > index && Made[index].Token is not null;
            });
            lock (Made) return Made[index];
        }
    }

    private sealed class FakeSocket(bool refuse) : IVpsSocket
    {
        private readonly Channel<string?> _in = Channel.CreateUnbounded<string?>();
        private readonly Channel<string> _out = Channel.CreateUnbounded<string>();

        public string? Token { get; private set; }
        public bool Disposed { get; private set; }
        public int Unread => _out.Reader.Count;

        public void Receive(string frame) => _in.Writer.TryWrite(frame);

        public void Close() => _in.Writer.TryWrite(null);

        public async Task<JsonElement> NextAsync()
        {
            using var limit = new CancellationTokenSource(Wait);
            return JsonDocument.Parse(await _out.Reader.ReadAsync(limit.Token)).RootElement.Clone();
        }

        public Task ConnectAsync(Uri url, string token, CancellationToken ct)
        {
            if (refuse) throw new VpsSocketException("the VPS did not accept the worker token");
            Token = token;
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken ct)
        {
            _out.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _in.Reader.ReadAsync(ct);

        public void Dispose() => Disposed = true;
    }

    private sealed class Route : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public List<string> Ended { get; } = [];
        public string? Why { get; set; }
        public Task? Hold { get; set; }
        public Func<AiJob, AiJobResult> Answer { get; set; } = job => new AiJobResult("ok", [], [], null) { Agent = "Codex" };
        public string Device => "PC-HUNG";
        public string? Unavailable() => Why;

        public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            if (Hold is { } hold) await hold.WaitAsync(ct);
            return Answer(job);
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;

        public void End(string conversationId)
        {
            lock (Ended) Ended.Add(conversationId);
        }
    }

    /// <summary>An agent that keeps what it was given and answers "ok".</summary>
    private sealed class Capture : IAssistantChatFactory, IAssistantChat
    {
        public string? System { get; private set; }
        public IReadOnlyList<AssistantTool> Tools { get; private set; } = [];
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            (System, Tools) = (system, tools);
            return this;
        }

        public void AddUser(string text) { }

        public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(new AssistantStep("ok", [], new AssistantUsage(10, 1, 0, 0), false));

        public void AddResults(IReadOnlyList<ToolOutcome> results) { }
    }

    /// <summary>A Helm tool that would be offered to Ask Helm, never to a VPS job.</summary>
    private sealed class AnyTool : IMcpToolProvider
    {
        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools =>
            [new("notes_search", "Search notes.", McpTool.NoArguments(), (_, _) => Task.FromResult<object?>("[]")) { ReadOnly = true }];
    }
}

