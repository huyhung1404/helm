using Helm.Modules.Wallet;

namespace Helm.Modules.Tracker;

/// <summary>
/// Copies the old debt book (Helm 0.26 and older kept debts in a Tracker workspace) into Wallet's debt book. Each entry
/// keeps its id, so doing it on two devices, or again, writes the same records; copied entries are marked, so one
/// deleted in Wallet does not come back. The Tracker records stay for older versions. Safe to call often: it does
/// nothing once everything is copied.
/// </summary>
public sealed class TrackerDebtHandover(TrackerStore tracker, DebtBook debts)
{
    private readonly object _gate = new();

    /// <returns>How many entries were copied.</returns>
    public int Run()
    {
        lock (_gate)
        {
            var pending = tracker.DebtsToMove();
            if (pending.Count == 0) return 0;
            var copied = debts.Import(pending.Select(p => (p.Id, ToWallet(p.Item))));
            tracker.MarkMoved(pending.Select(p => p.Id));
            return copied;
        }
    }

    internal static WalletDebt ToWallet(TrackerItem item) => new()
    {
        Person = item.Person.Trim().Length > 0 ? item.Person : "(no name)",
        Amount = Math.Abs(item.Amount),
        Direction = item.Direction,
        IsRepayment = item.IsRepayment,
        Note = string.Join(" — ", new[] { item.Title.Trim(), item.Notes.Trim() }.Where(s => s.Length > 0)),
        DueDate = item.DueDate,
        DueAt = item.DueAt,
        CreatedAt = item.CreatedAt,
        SettledAt = item.CompletedAt,
    };
}
