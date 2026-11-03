namespace Helm.Ai.Assistant;

/// <summary>
/// One turn of a conversation for Claude Code to run: the user's words, the Helm tools it may use (by their MCP names),
/// the phone tools it may hand back, and the Claude model. A conversation keeps its id across turns, so a worker can
/// keep one Claude Code process for it.
/// </summary>
public sealed record AiJob(
    string Feature,
    string ConversationId,
    string Text,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> PhoneTools,
    string Model,
    string Effort)
{
    /// <summary>The job's own system prompt (a VPS service's job); null for Ask Helm's instructions.</summary>
    public string? System { get; init; }

    /// <summary>
    /// The photos the user sent with the words (a receipt, a whiteboard), for an agent that reads pictures
    /// (<see cref="IAgentRoute.ReadsPhotos"/>). Never written into a synced record: between devices each one travels as a
    /// blob of its own (<see cref="AiJobRecord.Photos"/>), gone once the turn is over.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<AssistantImage> Images { get; init; } = [];
}

/// <summary>A change Claude Code made with one of Helm's tools on the PC, with the tool's result (for Undo).</summary>
public sealed record AiJobAction(string Tool, string Summary, string ResultJson)
{
    /// <summary>The Helm PC (its sync device id) that made it, where its Undo goes; null when it was made on this device.</summary>
    public string? Worker { get; init; }
}

/// <summary>A phone tool Claude Code asked for (alarm, calendar…): the phone runs it when the result arrives.</summary>
public sealed record AiJobPhoneAction(string Tool, string ArgumentsJson, string Summary);

/// <param name="SavedUsd">What the same turn would have cost on the API with the picked model, if known.</param>
public sealed record AiJobResult(string Text, IReadOnlyList<AiJobAction> Done, IReadOnlyList<AiJobPhoneAction> PhoneActions, decimal? SavedUsd)
{
    /// <summary>The agent that ran it ("Codex", "Claude Code").</summary>
    public string? Agent { get; init; }

    /// <summary>The Helm PC that ran it, as the user knows it, when it ran through sync (one of several PCs).</summary>
    public string? Device { get; init; }
}

/// <summary>The agent could not take or finish the job in time; the router tries the next way.</summary>
public class AiRouteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// An agent took the turn and then failed or ran out of time: it may have done part of it, so the turn must not run
/// again anywhere else (the API would do it a second time). Said to the user as is.
/// </summary>
public sealed class AiRouteTakenException(string message) : AiRouteException(message);

/// <summary>
/// A way to an AI agent on the user's own plan (Claude Code first; Codex, Gemini CLI or a local model can follow): on
/// this PC itself, or on another Helm PC through Helm Sync. Helm runs its own tools there for it; what they write syncs
/// to every device.
/// </summary>
public interface IAgentRoute
{
    /// <summary>The device that runs the jobs, as the user knows it ("this PC", the PC's name).</summary>
    string Device { get; }

    /// <summary>Why it cannot take a job now (not installed, offline, busy at its limit), or null when it can.</summary>
    string? Unavailable();

    /// <exception cref="AiRouteException">No answer within <paramref name="wait"/>, or it failed.</exception>
    Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct);

    /// <summary>Takes back an action it did (Undo runs where the record was made, so it never misses one not synced yet).</summary>
    Task UndoAsync(AiJobAction action, CancellationToken ct);

    /// <summary>The conversation is over: the process kept for it may stop.</summary>
    void End(string conversationId);

    /// <summary>
    /// The user is about to ask in this conversation (the card opened): its agent gets ready (a process started, the
    /// way to it open) so the first turn answers sooner. <paramref name="job"/> has no text yet: its model and tools.
    /// Returns at once; a warm-up that no turn follows ends with <see cref="End"/> or after the agent's idle time.
    /// </summary>
    void Warm(AiJob job) { }

    /// <summary>It can take a job with photos (<see cref="AiJob.Images"/>) now: an agent that reads pictures is ready.</summary>
    bool ReadsPhotos => false;
}

/// <summary>The ways a feature's call may take, in order: Claude Code first (free) as its policy allows, the API last.</summary>
public sealed class AiRouter(IEnumerable<IAgentRoute> routes)
{
    private readonly IReadOnlyList<IAgentRoute> _routes = [.. routes];

    /// <summary>
    /// The agent ways to try, best first, whatever model is picked (that one is for the API, when no agent answers):
    /// none for <see cref="AiRoutePolicy.ApiOnly"/>, and only those that can take a job now.
    /// </summary>
    public IReadOnlyList<IAgentRoute> Agents(AiFeatureChoice choice) =>
        choice.Route == AiRoutePolicy.ApiOnly
            ? []
            // An agent on this device answers sooner than one reached through sync.
            : [.. _routes.Where(r => r.Unavailable() is null).OrderBy(r => r is SyncAgentRoute ? 1 : 0)];

    /// <summary>Whether the API may be used when no agent answered.</summary>
    public static bool MayUseApi(AiFeatureChoice choice) => choice.Route != AiRoutePolicy.PcOnly;

    /// <summary>Why a <see cref="AiRoutePolicy.PcOnly"/> call found no PC, said to the user.</summary>
    public string NoPc() => _routes.Count == 0
        ? "No Helm PC has an agent for this (AI → Proxy on the PC)."
        : $"No Helm PC agent can take it now ({string.Join("; ", _routes.Select(r => $"{r.Device}: {r.Unavailable() ?? "busy"}"))}).";
}
