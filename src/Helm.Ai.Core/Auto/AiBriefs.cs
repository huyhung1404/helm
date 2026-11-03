using Helm.Ai.Assistant;
using Helm.Core.Sync;

namespace Helm.Ai.Auto;

/// <summary>
/// A short result of a job Helm ran by itself on a PC (the morning brief, the spending digest…), for the phone to show
/// as a notification and Ask Helm to read out. Synced (collection <see cref="AiBriefBook.Collection"/>).
/// </summary>
public sealed record AiBrief
{
    /// <summary>The job's <see cref="AiFeatureIds"/> id.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the user read it (Ask Helm read it out); null while new.</summary>
    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>The PC that wrote it.</summary>
    public string? Device { get; init; }

    /// <summary>What the job changed, each with its tool's result (for Undo): the titles it gave notes.</summary>
    public IReadOnlyList<AiJobAction> Actions { get; init; } = [];

    public DateTimeOffset? UndoneAt { get; init; }
}

/// <summary>The briefs: written by the PC's jobs, read on every device, cleared after <see cref="KeepFor"/>.</summary>
public sealed class AiBriefBook(ISyncedCollection<AiBrief> briefs, TimeProvider? time = null)
{
    public const string Collection = "ai.briefs";

    /// <summary>A brief longer than this is cut (it is read on a phone).</summary>
    public const int MaxText = 2000;

    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public event EventHandler<SyncedChangedEventArgs>? Changed
    {
        add => briefs.Changed += value;
        remove => briefs.Changed -= value;
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    public string Publish(string kind, string title, string text, IReadOnlyList<AiJobAction>? actions = null, string? device = null)
    {
        text = text.Trim();
        if (text.Length > MaxText) text = text[..(MaxText - 1)] + "…";
        return briefs.Add(new AiBrief
        {
            Kind = kind,
            Title = title.Trim(),
            Text = text,
            CreatedAt = Now,
            Device = device,
            Actions = actions ?? [],
        });
    }

    public AiBrief? Get(string id) => briefs.Get(id);

    /// <summary>Newest first, of one kind or all.</summary>
    public IReadOnlyList<SyncedItem<AiBrief>> Latest(int limit = 5, string? kind = null) =>
        [.. briefs.All().Where(b => kind is null || b.Value.Kind == kind).OrderByDescending(b => b.Value.CreatedAt).Take(limit)];

    public void MarkRead(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (briefs.Get(id) is { ReadAt: null } brief) briefs.Upsert(id, brief with { ReadAt = Now });
    }

    public void MarkUndone(string id)
    {
        if (briefs.Get(id) is { } brief) briefs.Upsert(id, brief with { UndoneAt = Now });
    }

    /// <summary>Takes away briefs older than <see cref="KeepFor"/>; how many went.</summary>
    public int Clean()
    {
        var old = briefs.All().Where(b => Now - b.Value.CreatedAt > KeepFor).Select(b => b.Id).ToList();
        foreach (var id in old) briefs.Delete(id);
        return old.Count;
    }
}
