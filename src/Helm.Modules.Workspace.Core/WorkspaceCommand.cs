namespace Helm.Modules.Workspace;

public enum WorkspaceCommandKind
{
    /// <summary>Type a prompt into an open session.</summary>
    Prompt,
    /// <summary>Open a new Claude tab in a VS Code window, optionally switch its model, then type the prompt.</summary>
    NewSession,
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

    public required string Text { get; init; }

    /// <summary>New session only: a model alias for <c>/model</c> (opus, sonnet, haiku …); null keeps the default.</summary>
    public string? Model { get; init; }

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
