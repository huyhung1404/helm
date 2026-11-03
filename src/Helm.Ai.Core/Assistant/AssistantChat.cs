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
/// <param name="WebSearches">Searches the provider ran on the web for this answer (each one is paid, <see cref="AiPrices.WebSearchUsd"/>).</param>
public sealed record AssistantUsage(long Input, long Output, long CacheRead, long CacheWrite, long WebSearches = 0);

/// <summary>One answer of the model: its text, the tools it calls, what it cost.</summary>
public sealed record AssistantStep(string Text, IReadOnlyList<ToolCall> Calls, AssistantUsage Usage, bool Refused);

/// <summary>
/// One conversation with one provider's model. It keeps its own history in the provider's shape (Claude's thinking
/// blocks, for one, must go back unchanged), so the assistant above it stays the same for every provider.
/// </summary>
public interface IAssistantChat
{
    void AddUser(string text);

    /// <summary>
    /// The user's words with a photo (<see cref="AssistantImage"/>), for a model that reads pictures. The next
    /// <see cref="NextAsync"/> throws <see cref="AssistantImageException"/> when the model turns the picture down.
    /// </summary>
    void AddUser(string text, AssistantImage image) => throw new AssistantImageException("This model cannot read photos.");

    /// <summary>Sends the conversation; the model's answer is added to it.</summary>
    Task<AssistantStep> NextAsync(CancellationToken ct);

    /// <summary>The results of the calls of the last step, one per call.</summary>
    void AddResults(IReadOnlyList<ToolOutcome> results);

    /// <summary>
    /// More tools from the next call on: a later turn asked for a group the conversation did not need before
    /// (<see cref="ToolScope"/>).
    /// </summary>
    void AddTools(IReadOnlyList<AssistantTool> tools) { }

    /// <summary>
    /// The provider's own web search from the next call on, at most <paramref name="maxUses"/> searches per call
    /// (<see cref="WebSearch"/>). False when this provider has none here (the caller asks Claude instead).
    /// </summary>
    bool AddWebSearch(int maxUses) => false;
}

/// <summary>Opens conversations with one provider's models.</summary>
public interface IAssistantChatFactory
{
    AiProvider Provider { get; }

    /// <summary>
    /// An agent CLI's chats take photos (<see cref="IAssistantChat.AddUser(string, AssistantImage)"/>): one that cannot
    /// gets no photo job, and the next agent or the API reads it. The API's chats are checked per model instead
    /// (<see cref="AssistantPhotos.Reads"/>).
    /// </summary>
    bool ReadsImages => false;

    IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools);
}

/// <summary>A photo for the model: the bytes as sent (a JPEG made small on the phone) and their media type.</summary>
public sealed record AssistantImage(byte[] Data, string MediaType = "image/jpeg");

/// <summary>
/// The model does not take pictures (the provider said so for the photo just added): the caller may ask another model.
/// Not an <see cref="AssistantException"/>, so it leaves <see cref="AssistantLoop"/> instead of ending the turn there.
/// </summary>
public sealed class AssistantImageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The provider could not be used for this call (key rejected, no credit, no internet…): said to the user as is.</summary>
public sealed class AssistantException(string message, Exception? inner = null) : Exception(message, inner);
