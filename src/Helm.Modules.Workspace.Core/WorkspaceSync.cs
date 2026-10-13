using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Workspace;

/// <summary>A VS Code window of a host, as the phone lists it (one tab each).</summary>
public sealed record HostWindowInfo
{
    /// <summary>Its first folder, which also names where a new session opens.</summary>
    public string Folder { get; init; } = "";
    public string Name { get; init; } = "";
    public int ClaudeTabs { get; init; }
    public bool Connected { get; init; } = true;
}

/// <summary>A slash command or skill Claude Code offers (for the "/" list on the phone).</summary>
public sealed record SkillInfo(string Name, string Description);

/// <summary>
/// One PC running Workspace Controller (<c>workspace.hosts</c>, id = <see cref="MachineId"/>). The PC writes it when
/// its windows change and once a minute, so the phone can tell an online PC from one that is off.
/// </summary>
public sealed record WorkspaceHostRecord
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>False once the tool was turned off (or Helm closed) on that PC.</summary>
    public bool Running { get; init; }

    public IReadOnlyList<HostWindowInfo> Windows { get; init; } = [];

    /// <summary>Model aliases for a new session (<c>/model …</c>).</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Slash commands and skills, for the "/" suggestions.</summary>
    public IReadOnlyList<SkillInfo> Skills { get; init; } = [];

    /// <summary>The PC wrote within this long and its tool is on: it is reachable.</summary>
    public static readonly TimeSpan OnlineWithin = TimeSpan.FromMinutes(3);

    public bool IsOnline(DateTimeOffset now) => Running && now - LastSeen < OnlineWithin;
}

/// <summary>
/// One connected Claude session (<c>workspace.sessions</c>, id = session id), written by its PC. A disconnected or
/// gone session is deleted. Kept small: the last messages only, each shortened.
/// </summary>
public sealed record WorkspaceSessionRecord
{
    public string MachineId { get; init; } = "";
    public string SessionId { get; init; } = "";

    /// <summary>The VS Code window's folder it belongs to (<see cref="HostWindowInfo.Folder"/>); empty outside VS Code.</summary>
    public string WindowFolder { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Title { get; init; } = "";
    public SessionState State { get; init; }
    public string? Waiting { get; init; }
    public string? LastPrompt { get; init; }
    public string? LastReply { get; init; }
    public IReadOnlyList<TranscriptTurn> Recent { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? WorkingSince { get; init; }
}

/// <summary>How far a command got (<c>workspace.results</c>, id = command id), written by the PC that ran it.</summary>
public sealed record WorkspaceResultRecord
{
    public string CommandId { get; init; } = "";
    public string MachineId { get; init; } = "";
    public WorkspaceCommandStatus Status { get; init; }
    public string? Error { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The synced collections of Workspace Controller (all end-to-end encrypted like the rest of Helm Sync).</summary>
public static class WorkspaceSync
{
    public const string Prefix = "workspace.";
    public const string Hosts = "workspace.hosts";
    public const string Sessions = "workspace.sessions";
    public const string Commands = "workspace.commands";
    public const string Results = "workspace.results";

    /// <summary>Text longer than this is cut (records stay far below the server's 64 KB).</summary>
    public const int MaxText = 6000;

    /// <summary>Messages kept per session record.</summary>
    public const int MaxTurns = 8;

    /// <summary>Commands and results older than this are removed by the PC.</summary>
    public static readonly TimeSpan KeepCommands = TimeSpan.FromMinutes(30);

    /// <summary>The collections and the "What syncs on this device" entry; both apps call it.</summary>
    public static IServiceCollection AddWorkspaceSync(this IServiceCollection services)
    {
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceHostRecord> { Name = Hosts });
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceSessionRecord> { Name = Sessions });
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceCommand> { Name = Commands });
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceResultRecord> { Name = Results });
        services.AddSyncGroup(Prefix, "Workspace Controller");
        return services;
    }

    /// <summary>A text cut to <see cref="MaxText"/> (the start kept; the phone shows "…").</summary>
    public static string? Cut(string? text) => text is null || text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";

    /// <summary>The record the phone sees for a session.</summary>
    public static WorkspaceSessionRecord ToRecord(AgentSession s, string windowFolder, IReadOnlyList<TranscriptTurn> recent) => new()
    {
        MachineId = s.MachineId,
        SessionId = s.SessionId,
        WindowFolder = windowFolder,
        Folder = s.Folder,
        Title = s.Title,
        State = s.State,
        Waiting = Cut(s.Waiting),
        LastPrompt = Cut(s.LastPrompt),
        LastReply = Cut(s.LastReply),
        Recent = recent.TakeLast(MaxTurns).Select(t => t with { Text = Cut(t.Text) ?? "" }).ToList(),
        UpdatedAt = s.UpdatedAt,
        WorkingSince = s.WorkingSince,
    };

    /// <summary>Same content (the time of a write alone is no reason to sync again).</summary>
    public static bool SameContent(WorkspaceSessionRecord a, WorkspaceSessionRecord b) =>
        a with { UpdatedAt = default, Recent = [] } == b with { UpdatedAt = default, Recent = [] } && a.Recent.SequenceEqual(b.Recent);
}
