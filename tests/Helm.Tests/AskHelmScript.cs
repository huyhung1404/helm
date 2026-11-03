using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;

namespace Helm.Tests;

/// <summary>
/// Ask Helm with a scripted model: the steps it answers, in order, and what it was sent. Shared by the tests of
/// Ask Helm's tools (they go through the same loop, Undo and scope as a real model's calls).
/// </summary>
internal sealed class AskHelmScript : IAssistantChatFactory
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";

    private readonly Queue<AssistantStep> _steps;

    public AskHelmScript(params AssistantStep[] steps) => _steps = new Queue<AssistantStep>(steps);

    public AiProvider Provider => AiProviders.Claude;

    /// <summary>The tools each chat was offered.</summary>
    public List<IReadOnlyList<AssistantTool>> Offered { get; } = [];

    /// <summary>What the tools answered, step by step.</summary>
    public List<IReadOnlyList<ToolOutcome>> Results { get; } = [];

    public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
    {
        Offered.Add(tools);
        return new Chat(this);
    }

    /// <summary>The assistant on the API with these tools (and a tested Claude key).</summary>
    public HelmAssistant Assistant(params IMcpToolProvider[] providers)
    {
        var keys = new AiKeyStore(new MemorySynced<AiKey>());
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey, new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.",
            [new AiModel("claude-haiku-5-5", "Claude Haiku 5.5")]));
        return new HelmAssistant(keys, new MemorySettings<AiFeatureSettings>(), new AiUsageBook(new MemorySyncedLog<AiUsageEntry>()), providers, [this], _ => true);
    }

    /// <summary>One turn said to a new card, every yes given.</summary>
    public Task<AssistantReply> Say(string text, IMcpToolProvider[] providers, IMcpToolProvider[]? phone = null) =>
        Assistant(providers).Start(phone).SendAsync(text, _ => Task.FromResult(true), CancellationToken.None);

    public static AssistantStep Step(params ToolCall[] calls) => new("", calls, new AssistantUsage(100, 10, 0, 0), false);

    public static AssistantStep Answer(string text) => new(text, [], new AssistantUsage(100, 10, 0, 0), false);

    public static ToolCall Call(string name, string input) => new(Guid.NewGuid().ToString("N"), name, JsonDocument.Parse(input).RootElement.Clone());

    private sealed class Chat(AskHelmScript script) : IAssistantChat
    {
        public void AddUser(string text) { }

        public Task<AssistantStep> NextAsync(CancellationToken ct) =>
            Task.FromResult(script._steps.Count > 0 ? script._steps.Dequeue() : Answer(""));

        public void AddResults(IReadOnlyList<ToolOutcome> results) => script.Results.Add(results);
    }
}

/// <summary>The phone's tools as a test sees them: each call is kept, nothing opens.</summary>
internal sealed class FakePhone : IMcpToolProvider
{
    public List<(string Tool, JsonElement Args)> Calls { get; } = [];

    public string? ModuleId => null;

    public IEnumerable<McpTool> Tools => PhoneToolSpecs.All.Select(spec => PhoneToolSpecs.On(spec, (args, _) =>
    {
        Calls.Add((spec.Name, args.Clone()));
        return Task.FromResult<object?>(new { done = spec.Name });
    }));
}
