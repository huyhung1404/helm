using Helm.Core.Text;

namespace Helm.Modules.Wallet;

/// <summary>A polite message that reminds someone of what they owe the user, from the debt book. Helm never sends it.</summary>
public static class DebtReminder
{
    /// <summary>The person in the open debt book: the same name, else the only one whose name contains it.</summary>
    public static DebtPerson? Find(IReadOnlyList<DebtPerson> open, string name)
    {
        var key = DebtLedger.Key(name);
        if (open.FirstOrDefault(p => p.Key == key) is { } same) return same;
        var folded = TextSearch.Fold(name.Trim());
        var near = open.Where(p => TextSearch.Fold(p.Name).Contains(folded, StringComparison.Ordinal)).ToList();
        return near.Count == 1 ? near[0] : null;
    }

    /// <summary>
    /// "Chào Nam, mình nhắc nhẹ khoản 300.000đ (Ăn lẩu) từ hôm 28/10 nha. Khi nào tiện thì Nam chuyển giúp mình nhé,
    /// cảm ơn Nam nhiều!": the amount (the balance unless given), what it was for and since when, and the due day.
    /// </summary>
    public static string Message(DebtPerson person, decimal? amount, DateTimeOffset now, TimeZoneInfo zone)
    {
        var owed = person.Entries.Where(e => e.Kind == DebtEntryKind.OwesMe).ToList();
        var notes = owed.Select(e => e.Debt.Note.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(3).ToList();
        var today = WalletFormat.LocalDay(now, zone);
        var since = owed.Count > 0 ? WalletFormat.LocalDay(owed.Min(e => e.Debt.CreatedAt), zone) : (DateOnly?)null;
        var due = DebtDue.Day(person.Due(zone), zone);
        var text = $"Chào {person.Name}, mình nhắc nhẹ khoản {WalletSpeech.Money(amount ?? person.Balance)}";
        if (notes.Count > 0) text += $" ({string.Join(", ", notes)})";
        if (since is { } day && day < today) text += $" từ hôm {WalletSpeech.Day(day)}";
        text += " nha.";
        if (due is { } d) text += d < today ? $" Khoản này hẹn trả hôm {WalletSpeech.Day(d)} rồi." : d == today ? " Hôm nay là hạn trả rồi." : $" Hạn trả là {WalletSpeech.Day(d)}.";
        return text + $" Khi nào tiện thì {person.Name} chuyển giúp mình nhé, cảm ơn {person.Name} nhiều!";
    }
}
