using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>The sessions open as Claude tabs in this window, in tab order (the phone's subtabs).</summary>
    public IReadOnlyList<string> OpenSessions { get; init; } = [];

    /// <summary>The Claude tab in front in this window, when it is one Helm knows.</summary>
    public string? ActiveSession { get; init; }

    public bool Equals(HostWindowInfo? other) =>
        other is not null && Folder == other.Folder && Name == other.Name && ClaudeTabs == other.ClaudeTabs && Connected == other.Connected
        && OpenSessions.SequenceEqual(other.OpenSessions) && ActiveSession == other.ActiveSession;

    public override int GetHashCode() => HashCode.Combine(Folder, Name, ClaudeTabs, Connected, OpenSessions.Count, ActiveSession);
}

/// <summary>A slash command or skill Claude Code offers (for the "/" list on the phone).</summary>
public sealed record SkillInfo(string Name, string Description);

/// <summary>A model Claude Code offers (its own list, from the CLI's <c>initialize</c> answer).</summary>
public sealed record ModelInfo
{
    /// <summary>What <c>/model</c> takes: an alias (opus, sonnet …) or a model id; "default" for the default.</summary>
    public string Value { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>The model id it runs (claude-opus-5-5): matches a session's model.</summary>
    public string ResolvedModel { get; init; } = "";

    /// <summary>The effort levels it takes, lowest first; empty when it has no effort setting.</summary>
    public IReadOnlyList<string> EffortLevels { get; init; } = [];

    public bool Equals(ModelInfo? other) =>
        other is not null && Value == other.Value && DisplayName == other.DisplayName && Description == other.Description
        && ResolvedModel == other.ResolvedModel && EffortLevels.SequenceEqual(other.EffortLevels);

    public override int GetHashCode() => HashCode.Combine(Value, DisplayName, ResolvedModel);
}

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

    /// <summary>Model aliases for a new session (<c>/model …</c>); kept for phones of 0.42.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Claude Code's own model list, in its order.</summary>
    public IReadOnlyList<ModelInfo> ModelChoices { get; init; } = [];

    /// <summary>Auto mode is offered on this PC's account (it is one more stop of Shift+Tab).</summary>
    public bool AutoModeAvailable { get; init; }

    /// <summary>Slash commands and skills, for the "/" suggestions.</summary>
    public IReadOnlyList<SkillInfo> Skills { get; init; } = [];

    /// <summary>This PC takes pictures with a prompt (older ones would type the words alone).</summary>
    public bool TakesImages { get; init; }

    /// <summary>What the PC is busy with for the phone right now ("Typing a prompt into VS Code"), newest last.</summary>
    public IReadOnlyList<string> Activities { get; init; } = [];

    /// <summary>How often the PC writes this record when nothing changes (the phone's "last heard from").</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(30);

    /// <summary>The PC wrote within this long and its tool is on: it is reachable.</summary>
    public static readonly TimeSpan OnlineWithin = TimeSpan.FromMinutes(3);

    public bool IsOnline(DateTimeOffset now) => Running && now - LastSeen < OnlineWithin;
}

/// <summary>
/// One connected Claude session (<c>workspace.sessions</c>, id = session id), written by its PC. A disconnected or
/// gone session is deleted. Kept small: the end of the timeline, each step shortened.
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

    /// <summary>Plain messages, as 0.42 sent them (no longer filled: <see cref="Items"/> replaces it).</summary>
    public IReadOnlyList<TranscriptTurn> Recent { get; init; } = [];

    /// <summary>The end of the timeline: prompts, thoughts, tool calls with IN/OUT, answers.</summary>
    public IReadOnlyList<TranscriptItem> Items { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? WorkingSince { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The model id of the latest answer (claude-opus-5-5).</summary>
    public string? Model { get; init; }

    /// <summary>The effort of the latest answer (low, medium, high, xhigh, max).</summary>
    public string? Effort { get; init; }

    /// <summary>default (Manual), acceptEdits, plan, auto, bypassPermissions.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Context the latest answer used, and how much the model has.</summary>
    public long ContextTokens { get; init; }
    public long ContextWindow { get; init; }

    /// <summary>Subagents working for it now.</summary>
    public IReadOnlyList<SubagentInfo> Agents { get; init; } = [];

    /// <summary>What the PC holds for the phone to answer (permission, question, plan); null when nothing.</summary>
    public PendingRequest? Pending { get; init; }

    /// <summary>Outside VS Code, Helm on the PC runs it in the background for the phone (and can stop it).</summary>
    public bool Hosted { get; init; }

    /// <summary>
    /// Set when the timeline is in pages (<see cref="WorkspaceTimelinePage"/>): which writing of them, and the pages it
    /// has (oldest dropped). <see cref="Items"/> then keeps only the newest few, for phones before 0.58. Null: the
    /// timeline is <see cref="Items"/> alone, as before (an older PC, or a transcript without step ids).
    /// </summary>
    public string? TimelineEpoch { get; init; }
    public int TimelineFirstPage { get; init; }
    public int TimelineLastPage { get; init; }

    /// <summary>Not in a connected VS Code window (a terminal, another editor): the phone's "Outside VS Code" tab.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsOutside => WindowFolder.Length == 0;
}

/// <summary>
/// A stretch of a session's timeline (<c>workspace.timeline</c>, id = <see cref="WorkspaceSync.PageId"/>), written by
/// its PC: <see cref="WorkspaceSync.TimelinePageSize"/> steps, in order. A full page is written again only when one of
/// its steps changed (rare), so a live session rewrites only its last page while the phone keeps the earlier ones.
/// </summary>
public sealed record WorkspaceTimelinePage
{
    public string MachineId { get; init; } = "";
    public string SessionId { get; init; } = "";

    /// <summary>The writing it belongs to (<see cref="WorkspaceSessionRecord.TimelineEpoch"/>): a page of another one is not shown.</summary>
    public string Epoch { get; init; } = "";
    public int Page { get; init; }
    public IReadOnlyList<TranscriptItem> Items { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One earlier session of a folder, as the history sheet lists it.</summary>
public sealed record HistoryEntry
{
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTimeOffset LastAt { get; init; }
    public int Messages { get; init; }
    public string? Model { get; init; }
    public string FirstPrompt { get; init; } = "";
    public string LastReply { get; init; } = "";
}

/// <summary>
/// The sessions Claude Code kept for one folder of a PC (<c>workspace.history</c>, id = <see cref="WorkspaceSync.HistoryId"/>),
/// newest first: what the phone's History sheet shows and can reopen as a tab.
/// </summary>
public sealed record WorkspaceHistoryRecord
{
    public string MachineId { get; init; } = "";
    public string Folder { get; init; } = "";
    public IReadOnlyList<HistoryEntry> Sessions { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
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

    /// <summary>While <see cref="Status"/> is Sending: the step the PC is at ("Opening a new Claude tab in Helm").</summary>
    public string? Step { get; init; }

    /// <summary>
    /// Which start of Helm on the PC wrote it (null from PCs before 0.58). A Sending result of an earlier start was
    /// left behind when Helm stopped: the next start reports it failed instead of leaving it on its way for ever.
    /// </summary>
    public string? Run { get; init; }

    /// <summary>While a command is Sending (or waits its turn), its PC writes the result again this often: a lease.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(15);

    /// <summary>A Sending result not written again for this long (on a phone that is up to date) lost its PC.</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(60);
}

/// <summary>The synced collections of Workspace Controller (all end-to-end encrypted like the rest of Helm Sync).</summary>
public static class WorkspaceSync
{
    public const string Prefix = "workspace.";
    public const string Hosts = "workspace.hosts";
    public const string Sessions = "workspace.sessions";
    public const string Commands = "workspace.commands";
    public const string Results = "workspace.results";
    public const string History = "workspace.history";
    public const string Timeline = "workspace.timeline";

    /// <summary>Text longer than this is cut (records stay far below the server's 64 KB).</summary>
    public const int MaxText = 6000;

    /// <summary>Steps per timeline page, the pages kept per session (the oldest dropped), and the most a page may weigh.</summary>
    public const int TimelinePageSize = 20;
    public const int TimelinePages = 10;
    public const int MaxTimelineItems = TimelinePageSize * TimelinePages;
    public const int MaxPageBytes = 40_000;

    /// <summary>With pages, the session record still carries its newest steps, for phones before 0.58.</summary>
    public const int LegacyItems = 8;

    /// <summary>Messages kept per session record (0.42's plain list).</summary>
    public const int MaxTurns = 8;

    /// <summary>Timeline steps kept per session record, and the most they may weigh together.</summary>
    public const int MaxItems = 60;
    public const int MaxItemsBytes = 40_000;

    /// <summary>Earlier sessions listed per folder.</summary>
    public const int MaxHistory = 30;

    /// <summary>Commands and results older than this are removed by the PC.</summary>
    public static readonly TimeSpan KeepCommands = TimeSpan.FromMinutes(30);

    /// <summary>The collections and the "What syncs on this device" entry; both apps call it.</summary>
    public static IServiceCollection AddWorkspaceSync(this IServiceCollection services)
    {
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceHostRecord> { Name = Hosts });
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceSessionRecord> { Name = Sessions, BlobReferences = ImagesOf });
        // Someone holds the phone and waits: a command and the PC's progress go out at once.
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceCommand>
        {
            Name = Commands,
            Urgent = true,
            BlobReferences = c => c.Images.Select(i => i.Blob?.Id).OfType<string>(),
        });
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceResultRecord> { Name = Results, Urgent = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceHistoryRecord> { Name = History });
        services.AddSyncedCollection(new SyncedCollectionOptions<WorkspaceTimelinePage> { Name = Timeline, BlobReferences = p => ImagesOf(p.Items) });
        services.AddSyncGroup(Prefix, "Workspace Controller");
        return services;
    }

    /// <summary>The pictures a session record shows (kept on the server while it does).</summary>
    public static IEnumerable<string> ImagesOf(WorkspaceSessionRecord record) => ImagesOf(record.Items);

    public static IEnumerable<string> ImagesOf(IEnumerable<TranscriptItem> items) =>
        items.SelectMany(i => i.Images).Select(i => i.Blob?.Id).OfType<string>();

    /// <summary>The record id of a session's timeline page.</summary>
    public static string PageId(string sessionId, int page) => sessionId + "." + page.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The history record of one folder of one PC (the folder's path never shows in the id).</summary>
    public static string HistoryId(string machineId, string folder) =>
        machineId + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SessionRegistry.FolderKey(folder))))[..16];

    /// <summary>A text cut to <see cref="MaxText"/> (the start kept; the phone shows "…").</summary>
    public static string? Cut(string? text) => Cut(text, MaxText);

    public static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// How much context a model has. Claude Code does not say it per session; the current large models have a million
    /// tokens, older ones 200 000 (a session of a 1M model that is still small shows against 1M too).
    /// </summary>
    public static long ContextWindowOf(string? model, long used) =>
        used > 200_000 || model is not null && (model.StartsWith("claude-opus-5", StringComparison.Ordinal) || model.StartsWith("claude-fable", StringComparison.Ordinal)
            || model.StartsWith("claude-sonnet-5", StringComparison.Ordinal) || model.Contains("[1m]", StringComparison.Ordinal))
            ? 1_000_000
            : 200_000;

    /// <summary>The record the phone sees for a session.</summary>
    public static WorkspaceSessionRecord ToRecord(AgentSession s, string windowFolder, TranscriptView? view) => new()
    {
        MachineId = s.MachineId,
        SessionId = s.SessionId,
        WindowFolder = windowFolder,
        Folder = s.Folder,
        Title = s.Title.Length > 0 ? s.Title : view?.Title ?? "",
        State = s.State,
        Waiting = Cut(s.Waiting),
        LastPrompt = Cut(s.LastPrompt),
        LastReply = Cut(s.LastReply),
        Items = Fit(view?.Items ?? []),
        UpdatedAt = s.UpdatedAt,
        WorkingSince = s.WorkingSince,
        StartedAt = s.StartedAt,
        Model = view?.Model,
        Effort = view?.Effort,
        // The hooks tell the mode as it is now; the transcript only as of the last prompt.
        PermissionMode = s.PermissionMode ?? view?.PermissionMode,
        ContextTokens = view?.ContextTokens ?? 0,
        ContextWindow = view is { ContextTokens: > 0 } v ? ContextWindowOf(v.Model, v.ContextTokens) : 0,
        // Only a working session has subagents at work (a stopped turn leaves calls without results).
        Agents = s.State is SessionState.Working or SessionState.NeedsPermission or SessionState.Question ? view?.Agents ?? [] : [],
        Pending = s.Pending,
        Hosted = s.Hosted,
    };

    /// <summary>The newest steps that fit the record: answers cut, the oldest dropped first.</summary>
    internal static IReadOnlyList<TranscriptItem> Fit(IReadOnlyList<TranscriptItem> items)
    {
        var kept = items.TakeLast(MaxItems).Select(i => i with { Text = Cut(i.Text) ?? "", CallId = null }).ToList();
        var sizes = kept.Select(i => JsonSerializer.SerializeToUtf8Bytes(i).Length).ToList();
        var total = sizes.Sum();
        var drop = 0;
        while (total > MaxItemsBytes && drop < kept.Count - 1) total -= sizes[drop++];
        return drop == 0 ? kept : kept.GetRange(drop, kept.Count - drop);
    }

    /// <summary>Same content (the time of a write alone is no reason to sync again).</summary>
    public static bool SameContent(WorkspaceSessionRecord a, WorkspaceSessionRecord b) =>
        a with { UpdatedAt = default, Recent = [], Items = [], Agents = [] } == b with { UpdatedAt = default, Recent = [], Items = [], Agents = [] }
        && a.Items.SequenceEqual(b.Items) && a.Agents.SequenceEqual(b.Agents);
}
