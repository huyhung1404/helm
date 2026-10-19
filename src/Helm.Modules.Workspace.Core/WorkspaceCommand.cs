namespace Helm.Modules.Workspace;

public enum WorkspaceCommandKind
{
    /// <summary>Type a prompt into an open session.</summary>
    Prompt,
    /// <summary>Open a new Claude tab in a VS Code window, optionally switch its model, then type the prompt.</summary>
    NewSession,
    /// <summary>Answer what the PC holds for the phone: allow or deny a tool, answer Claude's question, decide on a plan.</summary>
    Answer,
    /// <summary>Switch an open session's model (<c>/model</c>, <see cref="WorkspaceCommand.Text"/> = alias or id).</summary>
    SetModel,
    /// <summary>Set an open session's effort (<c>/effort</c>, Text = low … max, "ultracode" or "ultracode off").</summary>
    SetEffort,
    /// <summary>Switch an open session's permission mode (Text = default, acceptEdits, plan, auto): Shift+Tab in its input.</summary>
    SetMode,
    /// <summary>Open an earlier session again as a Claude tab (<see cref="WorkspaceCommand.SessionId"/>, its folder).</summary>
    Reopen,
    /// <summary>Close a session's Claude tab (it stays in History); outside VS Code, take it off the phone's list.</summary>
    CloseTab,
    /// <summary>Stop the session Helm runs in the background (outside VS Code) where it is.</summary>
    Stop,
    /// <summary>Nothing to do: the PC answers at once and writes its host record, so the phone knows it is there now.</summary>
    Ping,
}

/// <summary>Something to do on one PC: always addressed to that PC (<see cref="MachineId"/>), never to "any".</summary>
public sealed record WorkspaceCommand
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string MachineId { get; init; }
    public WorkspaceCommandKind Kind { get; init; }

    /// <summary>The session to type into (<see cref="WorkspaceCommandKind.Prompt"/>).</summary>
    public string? SessionId { get; init; }

    /// <summary>The VS Code window's folder: where a new session opens, or where the session lives.</summary>
    public string Folder { get; init; } = "";

    /// <summary>
    /// The prompt (Prompt, NewSession); for Answer, a message Claude reads when you deny; the model, effort or mode
    /// for SetModel, SetEffort and SetMode; empty for Reopen and CloseTab.
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>Answer: the request (<see cref="PendingRequest.RequestId"/>).</summary>
    public string? RequestId { get; init; }

    /// <summary>Answer to a permission: allow (true) or deny.</summary>
    public bool Allow { get; init; }

    /// <summary>Answer to a question: question text → the chosen option(s) or your own words.</summary>
    public Dictionary<string, string>? Answers { get; init; }

    /// <summary>Answer to a plan: the mode it continues in (acceptEdits, default, auto), or null to keep planning.</summary>
    public string? PlanMode { get; init; }

    /// <summary>New session only: a model alias for <c>/model</c> (opus, sonnet, haiku …); null keeps the default.</summary>
    public string? Model { get; init; }

    /// <summary>New session only: an effort for <c>/effort</c>; null keeps the default.</summary>
    public string? Effort { get; init; }

    /// <summary>New session only: the permission mode to start in; null keeps the default.</summary>
    public string? Mode { get; init; }

    /// <summary>Prompt, NewSession: pictures sent with it (their blobs reach the server before this record does).</summary>
    public IReadOnlyList<WorkspaceImage> Images { get; init; } = [];

    /// <summary>True for the kinds that need <see cref="Text"/> (or, for a prompt, a picture).</summary>
    public bool NeedsText => Kind is WorkspaceCommandKind.Prompt or WorkspaceCommandKind.NewSession or WorkspaceCommandKind.SetModel
        or WorkspaceCommandKind.SetEffort or WorkspaceCommandKind.SetMode;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// A command older than this is not run (the phone may have been offline; typing it late would surprise). Some
    /// minutes, so a phone clock a little off from the PC's does not refuse everything.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
}

public enum WorkspaceCommandStatus
{
    /// <summary>Helm is typing it.</summary>
    Sending,
    /// <summary>Typed and Enter pressed; waiting for Claude to confirm.</summary>
    Sent,
    /// <summary>Claude's UserPromptSubmit hook carried the same text: it arrived.</summary>
    Received,
    Failed,
}

public sealed record WorkspaceCommandResult(string CommandId, WorkspaceCommandStatus Status, string? Error = null, string? SessionId = null);
