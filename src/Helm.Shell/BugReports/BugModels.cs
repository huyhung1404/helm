using Helm.Core.Sync;

namespace Helm.Shell.BugReports;

/// <summary>Where a bug is: <see cref="Open"/> → <see cref="Fixing"/> (an AI agent took it) → <see cref="ToVerify"/> → <see cref="Closed"/>.</summary>
public enum BugStatus
{
    Open,

    /// <summary>An AI agent claimed it (<see cref="BugReport.FixingBy"/>).</summary>
    Fixing,

    /// <summary>Fixed (by an agent or by hand), waiting for the user to check. An agent never closes a bug.</summary>
    ToVerify,

    Closed,
}

public enum BugSeverity
{
    /// <summary>"Blocks me": the user cannot do what they wanted.</summary>
    Blocking,
    Annoying,
    Minor,
}

public enum BugCloseReason
{
    Fixed,
    NotABug,
    Duplicate,
}

public enum BugDeviceKind
{
    Pc,
    Phone,
}

/// <summary>What one entry of a bug's conversation is.</summary>
public enum BugEntryKind
{
    /// <summary>The report itself (its black box and images).</summary>
    Report,

    /// <summary>The user added details.</summary>
    Details,

    /// <summary>An AI agent wrote how it is going.</summary>
    AgentNote,

    /// <summary>An AI agent asked the user something (<see cref="BugReport.PendingQuestion"/> until answered).</summary>
    Question,

    /// <summary>The user answered the agent's question.</summary>
    Answer,

    /// <summary>The status changed (claimed, resolved, closed, reopened…).</summary>
    Status,
}

/// <summary>
/// A bug the user reported, synced (collection <see cref="BugStore.ReportsCollection"/>). Its conversation, black
/// boxes and images are <see cref="BugEntry"/> records. Small and edited as a whole: the latest edit wins.
/// </summary>
public sealed record BugReport
{
    /// <summary>The short number people use: #B12.</summary>
    public int Number { get; init; }

    /// <summary>The first line of what the user wrote.</summary>
    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    public BugSeverity Severity { get; init; } = BugSeverity.Annoying;

    /// <summary>The tool (or "Helm") the bug is in, as shown: "Tracker".</summary>
    public string Tool { get; init; } = "";

    public string Device { get; init; } = "";

    public BugDeviceKind DeviceKind { get; init; }

    /// <summary>The Helm version that reported it.</summary>
    public string AppVersion { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public BugStatus Status { get; init; }

    public DateTimeOffset StatusChangedAt { get; init; }

    /// <summary>While <see cref="BugStatus.Fixing"/>: who works on it ("Claude Code · helm").</summary>
    public string? FixingBy { get; init; }

    /// <summary>The branch of the fix, when the agent said.</summary>
    public string? Branch { get; init; }

    /// <summary>The Helm version that has the fix ("0.86.0"), when known.</summary>
    public string? FixedIn { get; init; }

    /// <summary>The cause and the fix, as the agent told it.</summary>
    public string? Resolution { get; init; }

    public BugCloseReason? CloseReason { get; init; }

    /// <summary>With <see cref="BugCloseReason.Duplicate"/>: "#B7".</summary>
    public string? DuplicateOf { get; init; }

    /// <summary>The id of an agent's question still waiting for the user's answer.</summary>
    public string? PendingQuestion { get; init; }

    /// <summary>Reported from the black box of a crash.</summary>
    public bool FromCrash { get; init; }

    public string Code => $"B{Number}";
}

/// <summary>A picture attached to a report, an answer or details (the user picks it; Helm never takes screenshots).</summary>
public sealed record BugAttachment(BlobRef Blob, string MimeType, string? Name = null);

/// <summary>One entry of a bug's conversation (collection <see cref="BugStore.EntriesCollection"/>); never edited.</summary>
public sealed record BugEntry
{
    public string BugId { get; init; } = "";

    public BugEntryKind Kind { get; init; }

    /// <summary>"You" for the user, else the agent's name ("Claude Code").</summary>
    public string Author { get; init; } = "";

    public bool ByAgent { get; init; }

    /// <summary>The device it was written on (or the agent's project: "helm").</summary>
    public string Device { get; init; } = "";

    public string Text { get; init; } = "";

    public DateTimeOffset At { get; init; }

    /// <summary>The frozen black box (gzip'd JSON of a <see cref="Helm.Core.Diagnostics.BlackBoxSnapshot"/>), if one was attached.</summary>
    public BlobRef? BlackBox { get; init; }

    public int BlackBoxEvents { get; init; }

    public int BlackBoxErrors { get; init; }

    public List<BugAttachment> Images { get; init; } = [];

    /// <summary>For <see cref="BugEntryKind.Status"/>: the status it moved to.</summary>
    public BugStatus? NewStatus { get; init; }
}

/// <summary>A picture the user picked, before it is saved: its bytes, type and file name.</summary>
public sealed record BugImageDraft(byte[] Data, string MimeType, string? Name = null);
