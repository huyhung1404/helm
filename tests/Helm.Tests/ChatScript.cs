using Helm.Ai;
using Helm.Ai.Assistant;

namespace Helm.Tests;

/// <summary>A scripted model: the answers it gives, in order, and what each chat was opened with.</summary>
internal sealed class ChatScript : IAssistantChatFactory
{
    private readonly Queue<AssistantStep> _steps;

    public ChatScript(params AssistantStep[] steps) => _steps = new Queue<AssistantStep>(steps);

    public AiProvider Provider { get; init; } = AiProviders.Claude;

    /// <summary>The tools each chat was offered.</summary>
    public List<IReadOnlyList<AssistantTool>> Offered { get; } = [];

    /// <summary>The system prompt each chat was opened with.</summary>
    public List<string> Systems { get; } = [];

    /// <summary>What the user said, turn by turn, in every chat.</summary>
    public List<string> Said { get; } = [];

    public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
    {
        lock (_steps)
        {
            Offered.Add(tools);
            Systems.Add(system);
        }
        return new Chat(this);
    }

    public static AssistantStep Answer(string text) => new(text, [], new AssistantUsage(100, 10, 0, 0), false);

    private sealed class Chat(ChatScript script) : IAssistantChat
    {
        public void AddUser(string text)
        {
            lock (script._steps) script.Said.Add(text);
        }

        public Task<AssistantStep> NextAsync(CancellationToken ct)
        {
            lock (script._steps) return Task.FromResult(script._steps.Count > 0 ? script._steps.Dequeue() : Answer(""));
        }

        public void AddResults(IReadOnlyList<ToolOutcome> results) { }
    }
}
