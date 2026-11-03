using System.Net;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Ai.Prompts;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Helm.Tests;

/// <summary>
/// The features' prompts from the user's Skill Hub: an override is used while fresh and sound, anything else falls back to
/// the built-in text; the fetch keeps, replaces or drops the cached override; the Skill Hub's answer and settings.
/// </summary>
public sealed class AiPromptsTests : IDisposable
{
    private const string BuiltIn = "Built-in for {book}.";

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
    private readonly MemorySynced<AiPromptOverride> _cache = new();
    private readonly ListLogger _log = new();
    private readonly FakeSource _source = new();
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private AiPrompts Prompts() => new(_cache, _log, _time);

    private AiPromptRefresher Refresher(AiPrompts prompts) => new(prompts, _source, ["novel"], _log, _time);

    [Fact]
    public void Without_an_override_the_built_in_text_is_used() =>
        Assert.Equal(BuiltIn, Prompts().Get("novel", BuiltIn));

    [Fact]
    public async Task A_fetched_override_replaces_the_built_in_text()
    {
        var prompts = Prompts();
        _source.Texts["helm-novel"] = "Mine for {book}.";

        await Refresher(prompts).RefreshAsync(CancellationToken.None);

        Assert.Equal("Mine for {book}.", prompts.Get("novel", BuiltIn));
        Assert.Equal("helm-novel", _cache.Get("novel")!.Name);
        // Only the feature asked for has one.
        Assert.Equal("Other.", prompts.Get("ask-helm", "Other."));
    }

    [Fact]
    public async Task An_override_missing_a_placeholder_is_ignored_and_logged_once()
    {
        var prompts = Prompts();
        _source.Texts["helm-novel"] = "Mine, without the book.";
        await Refresher(prompts).RefreshAsync(CancellationToken.None);

        Assert.Equal(BuiltIn, prompts.Get("novel", BuiltIn));
        Assert.Equal(BuiltIn, prompts.Get("novel", BuiltIn));

        var warning = Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains("{book}", warning.Text);
        // The text itself is never logged.
        Assert.DoesNotContain(_log.Lines, l => l.Text.Contains("without the book", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Plain text.", "Plain", null)]
    [InlineData("For {book} in {lang}.", "For {book}.", "it lacks the placeholder {lang}")]
    [InlineData("JSON {\"a\": 1} and {{book}}.", "No braces.", null)]
    [InlineData("For {book}.", "   ", "it is empty")]
    public void Problem_names_what_an_override_lacks(string builtIn, string text, string? problem) =>
        Assert.Equal(problem, AiPrompts.Problem(text, builtIn));

    [Fact]
    public void An_override_too_long_is_not_kept()
    {
        var prompts = Prompts();
        prompts.Save("novel", new string('x', AiPrompts.MaxLength + 1));

        Assert.Null(_cache.Get("novel"));
        Assert.Equal(BuiltIn, prompts.Get("novel", BuiltIn));
    }

    [Fact]
    public async Task A_failed_fetch_keeps_the_override_until_it_is_too_old()
    {
        var prompts = Prompts();
        _source.Texts["helm-novel"] = "Mine for {book}.";
        var refresher = Refresher(prompts);
        await refresher.RefreshAsync(CancellationToken.None);

        _source.Fails = true;
        _time.Advance(TimeSpan.FromDays(1));
        await refresher.RefreshAsync(CancellationToken.None);
        await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal("Mine for {book}.", prompts.Get("novel", BuiltIn));
        // Said once when it starts failing.
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);

        _time.Advance(AiPrompts.MaxAge);
        Assert.Equal(BuiltIn, prompts.Get("novel", BuiltIn));
    }

    [Fact]
    public async Task A_prompt_gone_from_the_hub_puts_the_feature_back_on_the_built_in_text()
    {
        var prompts = Prompts();
        _source.Texts["helm-novel"] = "Mine for {book}.";
        var refresher = Refresher(prompts);
        await refresher.RefreshAsync(CancellationToken.None);

        _source.Texts.Clear();
        await refresher.RefreshAsync(CancellationToken.None);

        Assert.Null(_cache.Get("novel"));
        Assert.Equal(BuiltIn, prompts.Get("novel", BuiltIn));
    }

    [Fact]
    public async Task The_same_text_is_written_again_only_to_keep_it_fresh()
    {
        var prompts = Prompts();
        _source.Texts["helm-novel"] = "Mine for {book}.";
        var refresher = Refresher(prompts);
        await refresher.RefreshAsync(CancellationToken.None);
        var first = _cache.Get("novel")!.CheckedAt;

        _time.Advance(TimeSpan.FromHours(1));
        await refresher.RefreshAsync(CancellationToken.None);
        Assert.Equal(first, _cache.Get("novel")!.CheckedAt);

        _time.Advance(AiPrompts.Reconfirm);
        await refresher.RefreshAsync(CancellationToken.None);
        Assert.Equal(_time.Now, _cache.Get("novel")!.CheckedAt);

        // A new version on the hub is taken at once.
        _source.Texts["helm-novel"] = "Newer for {book}.";
        await refresher.RefreshAsync(CancellationToken.None);
        Assert.Equal("Newer for {book}.", prompts.Get("novel", BuiltIn));
    }

    [Fact]
    public async Task Nothing_is_asked_until_the_hub_is_set_up()
    {
        _source.SetUp = false;
        await Refresher(Prompts()).RefreshAsync(CancellationToken.None);
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public void Every_feature_id_is_fetched() => Assert.Contains(AiFeatureIds.AskHelm, AiPromptRefresher.AllFeatures());

    [Fact]
    public void Ask_Helm_on_the_api_starts_with_the_override()
    {
        var prompts = Prompts();
        prompts.Save(AiFeatureIds.AskHelm, "Em là trợ lý của Anh.");
        var assistant = new HelmAssistant(new AiKeyStore(new MemorySynced<AiKey>()), new MemorySettings<AiFeatureSettings>(),
            new AiUsageBook(new MemorySyncedLog<AiUsageEntry>()), [], [], _ => true, prompts: prompts);

        Assert.Equal("Em là trợ lý của Anh.", assistant.SystemPrompt());
    }

    [Fact]
    public async Task Ask_Helm_on_a_pc_agent_starts_with_the_override_unless_the_job_brings_its_own()
    {
        var prompts = Prompts();
        prompts.Save(AiFeatureIds.AskHelm, "Em là trợ lý của Anh.");
        var agent = new Capture();
        using var local = new LocalAgentRoute("PC-HUNG", "Codex", agent, [], _ => true, () => null, prompts: prompts);

        await local.RunAsync(new AiJob(AiFeatureIds.AskHelm, "c1", "hi", [], [], "m", "low"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Em là trợ lý của Anh.", agent.System);

        await local.RunAsync(new AiJob("blog.summary", "c2", "hi", [], [], "m", "low") { System = "You summarise posts." },
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("You summarise posts.", agent.System);
    }

    [Fact]
    public void The_hub_answer_gives_the_text_or_none()
    {
        Assert.Equal("Hello", SkillHubPromptSource.Read("""{"jsonrpc":"2.0","id":1,"result":{"messages":[{"role":"user","content":{"type":"text","text":"Hello"}}]}}"""));
        Assert.Null(SkillHubPromptSource.Read("""{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Unknown prompt: p-helm-x"}}"""));
        Assert.Throws<AiPromptSourceException>(() => SkillHubPromptSource.Read("""{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Internal error"}}"""));
        Assert.Throws<AiPromptSourceException>(() => SkillHubPromptSource.Read("<html>Sign in</html>"));
        Assert.Throws<AiPromptSourceException>(() => SkillHubPromptSource.Read("""{"jsonrpc":"2.0","id":1,"result":{}}"""));
    }

    [Fact]
    public async Task The_hub_is_asked_with_both_tokens_for_the_saved_prompt()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var http = new HttpClient(new Handler(async r =>
        {
            sent = r;
            body = await r.Content!.ReadAsStringAsync();
            return (HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":{"messages":[{"role":"user","content":{"type":"text","text":"Mine"}}]}}""");
        }));
        var source = new SkillHubPromptSource(http, () => new SkillHubConnection(new Uri("https://hub.example/api/mcp"), "machine", "cf-id", "cf-secret"));

        Assert.Equal("Mine", await source.GetAsync("helm-ask-helm", CancellationToken.None));
        Assert.Equal("Bearer machine", sent!.Headers.Authorization!.ToString());
        Assert.Equal("cf-id", sent.Headers.GetValues("CF-Access-Client-Id").Single());
        Assert.Equal("cf-secret", sent.Headers.GetValues("CF-Access-Client-Secret").Single());
        Assert.Contains("\"method\":\"prompts/get\"", body);
        Assert.Contains("\"name\":\"p-helm-ask-helm\"", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_refused_request_is_a_failure_not_an_empty_prompt(HttpStatusCode status)
    {
        var http = new HttpClient(new Handler(_ => Task.FromResult((status, ""))));
        var source = new SkillHubPromptSource(http, () => new SkillHubConnection(new Uri("https://hub.example/api/mcp"), "machine", null, null));

        await Assert.ThrowsAsync<AiPromptSourceException>(() => source.GetAsync("helm-ask-helm", CancellationToken.None));
    }

    [Fact]
    public void Tokens_typed_into_the_settings_file_move_into_the_encrypted_file()
    {
        var paths = new HelmPaths(_dir.Path);
        using var factory = new SettingsStoreFactory(paths);
        var store = factory.Get<SkillHubSettings>(SkillHubSettings.StoreId);
        var secrets = new SkillHubSecrets(paths, new PlainSecretProtector());
        Assert.Null(secrets.Connection(store));

        Directory.CreateDirectory(paths.SettingsDirectory);
        File.WriteAllText(store.FilePath, """{ "version": 1, "url": "https://hub.example/api/mcp", "token": "machine", "accessClientId": "cf-id", "accessClientSecret": "cf-secret" }""");
        var connection = secrets.Connection(store);

        Assert.Equal(new SkillHubConnection(new Uri("https://hub.example/api/mcp"), "machine", "cf-id", "cf-secret"), connection);
        var file = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("machine", file);
        Assert.DoesNotContain("cf-secret", file);
        // Read again from the encrypted file.
        Assert.Equal(connection, secrets.Connection(store));

        // A new token typed in replaces only the token.
        File.WriteAllText(store.FilePath, """{ "version": 1, "url": "https://hub.example/api/mcp", "token": "machine-2" }""");
        Assert.Equal(connection! with { Token = "machine-2" }, secrets.Connection(store));

        // Never over plain http, except to this PC itself.
        File.WriteAllText(store.FilePath, """{ "version": 1, "url": "http://hub.example/api/mcp" }""");
        Assert.Null(secrets.Connection(store));
        File.WriteAllText(store.FilePath, """{ "version": 1, "url": "http://127.0.0.1:5000/api/mcp" }""");
        Assert.NotNull(secrets.Connection(store));
    }

    private sealed class FakeSource : IAiPromptSource
    {
        public Dictionary<string, string> Texts { get; } = [];
        public bool SetUp { get; set; } = true;
        public bool Fails { get; set; }
        public int Calls { get; private set; }

        public bool IsSetUp => SetUp;

        public Task<string?> GetAsync(string name, CancellationToken ct)
        {
            Calls++;
            if (Fails) throw new AiPromptSourceException("Could not reach the Skill Hub.");
            return Task.FromResult(Texts.GetValueOrDefault(name));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<(HttpStatusCode Status, string Body)>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = await answer(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Capture : IAssistantChatFactory, IAssistantChat
    {
        public string? System { get; private set; }
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            System = system;
            return this;
        }

        public void AddUser(string text) { }

        public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(new AssistantStep("ok", [], new AssistantUsage(10, 1, 0, 0), false));

        public void AddResults(IReadOnlyList<ToolOutcome> results) { }
    }
}

/// <summary>Keeps every formatted log line, to check what is logged (and what never is).</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Text)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception)));
}
