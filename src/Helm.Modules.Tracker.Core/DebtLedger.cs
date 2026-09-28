using Helm.Core.Sync;

namespace Helm.Modules.Tracker;

/// <summary>One entry of a person in the debt book, with the balance right after it.</summary>
public sealed record DebtLedgerEntry(string Id, TrackerItem Item, decimal BalanceAfter)
{
    public DebtEntryKind Kind => Item.IsRepayment ? DebtEntryKind.Repayment
        : Item.Direction == DebtDirection.TheyOweMe ? DebtEntryKind.OwesMe : DebtEntryKind.IOwe;
}

/// <summary>
/// A person in the debt book: every entry of theirs since they were last settled, and what it adds up to. A balance
/// of 0 means settled.
/// </summary>
public sealed record DebtPerson(string Key, string Name, decimal Balance, IReadOnlyList<DebtLedgerEntry> Entries, DateTimeOffset? SettledAt)
{
    public bool IsSettled => Balance == 0;

    public DateTimeOffset LastActivity => Entries.Count == 0 ? DateTimeOffset.MinValue : Entries.Max(e => e.Item.CreatedAt);

    /// <summary>When the person's open debt is due (the latest due time among the entries).</summary>
    public DateTimeOffset? Due(TimeZoneInfo zone) => Entries.Select(e => e.Item.DueMoment(zone)).Where(d => d is not null).Max();
}

/// <summary>Groups debt entries by person (the same name, whatever its case and spacing).</summary>
public static class DebtLedger
{
    /// <summary>The name with its spacing tidied up.</summary>
    public static string Clean(string person) => string.Join(' ', person.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>What makes two names the same person.</summary>
    public static string Key(string person) => Clean(person).ToLowerInvariant();

    /// <summary>People with open entries, the ones owing (or owed) the most first. A balance that is already 0 is settled.</summary>
    public static IReadOnlyList<DebtPerson> Open(IEnumerable<SyncedItem<TrackerItem>> items) =>
        items.Where(i => !i.Value.IsCompleted)
            .GroupBy(i => Key(i.Value.Person))
            .Select(g => Build(g.Key, g, null))
            .OrderByDescending(p => Math.Abs(p.Balance)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Each time a person was settled (their entries settled together), most recent first.</summary>
    public static IReadOnlyList<DebtPerson> Settled(IEnumerable<SyncedItem<TrackerItem>> items) =>
        items.Where(i => i.Value.CompletedAt is not null)
            .GroupBy(i => (Key: Key(i.Value.Person), At: i.Value.CompletedAt!.Value))
            .Select(g => Build(g.Key.Key, g, g.Key.At))
            .OrderByDescending(p => p.SettledAt)
            .ToList();

    private static DebtPerson Build(string key, IEnumerable<SyncedItem<TrackerItem>> entries, DateTimeOffset? settledAt)
    {
        var ordered = entries.OrderBy(e => e.Value.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var running = 0m;
        var rows = new List<DebtLedgerEntry>();
        foreach (var (id, item, _) in ordered)
        {
            running += item.SignedAmount;
            rows.Add(new DebtLedgerEntry(id, item, running));
        }
        var name = ordered.Select(e => e.Value.Person).FirstOrDefault(n => n.Length > 0) ?? "";
        return new DebtPerson(key, name.Length > 0 ? Clean(name) : "(no name)", settledAt is null ? running : 0, rows, settledAt);
    }
}
