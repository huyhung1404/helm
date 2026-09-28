using System.Text.Json.Serialization;

namespace Helm.Modules.Tracker;

/// <summary>What a workspace holds; decides which fields its items use.</summary>
public enum WorkspaceKind
{
    /// <summary>Things to do: title, priority, due date.</summary>
    Tasks,

    /// <summary>A debt book: who, how much and which way; "done" means settled.</summary>
    Debts,
}

public enum TrackerPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum DebtDirection
{
    /// <summary>The person owes the user.</summary>
    TheyOweMe,

    /// <summary>The user owes the person.</summary>
    IOwe,
}

/// <summary>A named list (synced record in <c>tracker.workspaces</c>).</summary>
public sealed record TrackerWorkspace
{
    public string Name { get; init; } = "";

    public WorkspaceKind Kind { get; init; }

    /// <summary>Debts only: the unit shown after amounts, e.g. "₫" or "USD".</summary>
    public string Currency { get; init; } = "";

    /// <summary>Position in the workspace list (ascending).</summary>
    public double Order { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One entry of a workspace (synced record in <c>tracker.items</c>).</summary>
public sealed record TrackerItem
{
    public string WorkspaceId { get; init; } = "";

    /// <summary>Tasks: what to do. Debts: what the money was for (may be empty).</summary>
    public string Title { get; init; } = "";

    public string Notes { get; init; } = "";

    public TrackerPriority Priority { get; init; } = TrackerPriority.Normal;

    /// <summary>Manual position among open items of the same priority (ascending).</summary>
    public double Order { get; init; }

    public DateOnly? DueDate { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When work began: set by Start, or on completion (= <see cref="CreatedAt"/>) if Start was never pressed.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>True when <see cref="StartedAt"/> came from the Start button rather than from completion.</summary>
    public bool StartedExplicitly { get; init; }

    // Debts
    public string Person { get; init; } = "";

    public decimal Amount { get; init; }

    public DebtDirection Direction { get; init; }

    [JsonIgnore]
    public bool IsCompleted => CompletedAt is not null;
}

public enum TrackerEventKind
{
    Created,
    Started,
    Completed,
    Reopened,
    Deleted,
}

/// <summary>
/// One line of the append-only history (<c>tracker.history</c>). Each event carries a snapshot of the item, so
/// reports keep working after the item is edited or deleted.
/// </summary>
public sealed record TrackerEvent
{
    public TrackerEventKind Kind { get; init; }

    public DateTimeOffset At { get; init; }

    public string ItemId { get; init; } = "";

    public string WorkspaceId { get; init; } = "";

    public WorkspaceKind WorkspaceKind { get; init; }

    public string Title { get; init; } = "";

    public TrackerPriority Priority { get; init; }

    public DateOnly? DueDate { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public bool StartedExplicitly { get; init; }

    public string Person { get; init; } = "";

    public decimal Amount { get; init; }

    public DebtDirection Direction { get; init; }

    public static TrackerEvent For(TrackerEventKind kind, DateTimeOffset at, string itemId, TrackerItem item, WorkspaceKind workspaceKind) => new()
    {
        Kind = kind,
        At = at,
        ItemId = itemId,
        WorkspaceId = item.WorkspaceId,
        WorkspaceKind = workspaceKind,
        Title = item.Title,
        Priority = item.Priority,
        DueDate = item.DueDate,
        CreatedAt = item.CreatedAt,
        StartedAt = item.StartedAt,
        CompletedAt = item.CompletedAt,
        StartedExplicitly = item.StartedExplicitly,
        Person = item.Person,
        Amount = item.Amount,
        Direction = item.Direction,
    };
}

/// <summary>What the user typed to add an item.</summary>
public sealed record TrackerItemDraft(
    string Title,
    TrackerPriority Priority = TrackerPriority.Normal,
    DateOnly? DueDate = null,
    string Notes = "",
    string Person = "",
    decimal Amount = 0,
    DebtDirection Direction = DebtDirection.TheyOweMe);
