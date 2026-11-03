using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Ai.Assistant;

/// <summary>What a tool may do without the user's say: read, save at once (it can be undone), or wait for a yes.</summary>
public enum AssistantToolMode
{
    Read,
    Auto,
    Confirm,
}

/// <summary>
/// One of Helm's tools as the assistant offers it to a model. A tool that changes something gets an extra
/// <c>summary</c> argument: the one line the user reads about that call (taken out before the tool runs).
/// </summary>
public sealed class AssistantTool
{
    public const string SummaryArgument = "summary";

    public AssistantTool(McpTool source, AssistantToolMode mode)
    {
        Source = source;
        Mode = mode;
        var schema = (JsonObject)source.InputSchema.DeepClone();
        if (mode != AssistantToolMode.Read)
        {
            var properties = schema["properties"] as JsonObject ?? [];
            properties[SummaryArgument] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "One short line in Vietnamese that the user reads about this call, e.g. \"Chi 45.000đ · Phở · Ăn uống · hôm nay\".",
            };
            schema["properties"] = properties;
            var required = schema["required"] as JsonArray ?? [];
            required.Add(SummaryArgument);
            schema["required"] = required;
        }
        Schema = schema;
    }

    public McpTool Source { get; }
    public AssistantToolMode Mode { get; }
    public string Name => Source.Name;
    public string Description => Source.Description;
    public JsonObject Schema { get; }
}

public sealed record ToolCall(string Id, string Name, JsonElement Input);

public sealed record ToolOutcome(string CallId, string Content, bool IsError);

/// <param name="Input">Input tokens not read from the cache.</param>
public sealed record AssistantUsage(long Input, long Output, long CacheRead, long CacheWrite);

/// <summary>One answer of the model: its text, the tools it calls, what it cost.</summary>
public sealed record AssistantStep(string Text, IReadOnlyList<ToolCall> Calls, AssistantUsage Usage, bool Refused);

/// <summary>
/// One conversation with one provider's model. It keeps its own history in the provider's shape (Claude's thinking
/// blocks, for one, must go back unchanged), so the assistant above it stays the same for every provider.
/// </summary>
public interface IAssistantChat
{
    void AddUser(string text);

    /// <summary>Sends the conversation; the model's answer is added to it.</summary>
    Task<AssistantStep> NextAsync(CancellationToken ct);

    /// <summary>The results of the calls of the last step, one per call.</summary>
    void AddResults(IReadOnlyList<ToolOutcome> results);
}

/// <summary>Opens conversations with one provider's models.</summary>
public interface IAssistantChatFactory
{
    AiProvider Provider { get; }

    IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools);
}

/// <summary>The provider could not be used for this call (key rejected, no credit, no internet…): said to the user as is.</summary>
public sealed class AssistantException(string message, Exception? inner = null) : Exception(message, inner);
