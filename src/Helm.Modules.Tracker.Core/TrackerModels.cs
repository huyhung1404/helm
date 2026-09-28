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

/// <summary>
/// What the user adds to a person's debts. A repayment is stored as the direction that pulls the balance toward 0 (plus
/// <see cref="TrackerItem.IsRepayment"/>), so older Helm versions, which know only the two directions, still add up the
/// same balance.
/// </summary>
public enum DebtEntryKind
{
    OwesMe,
    IOwe,
    Repayment,
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

    /// <summary>The due day (local). Kept next to <see cref="DueAt"/> for older Helm versions, which know only days.</summary>
    public DateOnly? DueDate { get; init; }

    /// <summary>The due date and time; null for an item due on a day (<see cref="DueDate"/>) or not at all.</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>Tasks: the item this is a subtask of (subtasks are items of their own, so older versions keep them).</summary>
    public string? ParentId { get; init; }

    /// <summary>Debts: this entry is a repayment (its <see cref="Direction"/> pulls the balance toward 0).</summary>
    public bool IsRepayment { get; init; }

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

    [JsonIgnore]
    public bool IsSubtask => ParentId is not null;

    /// <summary>+Amount when the person owes the user, −Amount when the user owes them.</summary>
    [JsonIgnore]
    public decimal SignedAmount => Direction == DebtDirection.TheyOweMe ? Amount : -Amount;

    /// <summary>When the item is due: its time, or the end of its due day (local) when only a day was set.</summary>
    public DateTimeOffset? DueMoment(TimeZoneInfo zone) => TrackerDue.Moment(DueAt, DueDate, zone);
}

/// <summary>Due dates with an optional time (DueAt), next to the day-only DueDate older versions read.</summary>
public static class TrackerDue
{
    public static DateTimeOffset? Moment(DateTimeOffset? dueAt, DateOnly? dueDate, TimeZoneInfo zone)
    {
        if (dueAt is { } at) return at;
        if (dueDate is not { } day) return null;
        var local = day.ToDateTime(new TimeOnly(23, 59));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The local day of a due time, written as DueDate so older versions see the right day.</summary>
    public static DateOnly? Day(DateTimeOffset? dueAt, TimeZoneInfo zone) =>
        dueAt is { } at ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime) : null;

    /// <summary>A date picked in the UI plus an optional time of day (local); null when no date.</summary>
    public static DateTimeOffset? FromLocal(DateTime? date, TimeSpan? time, TimeZoneInfo zone)
    {
        if (date is not { } d) return null;
        var local = d.Date + (time ?? new TimeSpan(23, 59, 0));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
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

    public DateTimeOffset? DueAt { get; init; }

    public string? ParentId { get; init; }

    public bool IsRepayment { get; init; }

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
        DueAt = item.DueAt,
        ParentId = item.ParentId,
        IsRepayment = item.IsRepayment,
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
    DebtDirection Direction = DebtDirection.TheyOweMe,
    DateTimeOffset? DueAt = null,
    string? ParentId = null);
