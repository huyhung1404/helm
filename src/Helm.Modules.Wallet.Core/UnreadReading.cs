namespace Helm.Modules.Wallet;

/// <summary>
/// "Read with AI" on the settings page's list of notifications Helm could not read: reads one when the user taps it
/// (<see cref="WalletNotificationAi"/>), and keeps what it saved at the top of the list for this session, with Undo,
/// until the user takes it back or leaves.
/// </summary>
internal sealed class UnreadReading(WalletNotificationAi? ai, Action<string> say, Action changed)
{
    private readonly List<(UnreadNotification Notification, NotificationRead Read)> _done = [];

    /// <summary>What AI read this session first (with Undo), then the notifications still unread (with Read with AI).</summary>
    public IReadOnlyList<UnreadRow> Rows(IEnumerable<UnreadNotification> unread, Func<UnreadNotification, string> when, Action<UnreadNotification> copy)
    {
        var rows = new List<UnreadRow>();
        foreach (var (notification, read) in _done)
        {
            var t = read.Transaction!;
            var text = (read.Duplicate ? "Already saved: " : "Saved: ") + WalletFormat.Signed(t.Amount)
                + (t.Description.Length > 0 ? " · " + t.Description : "") + (t.Balance is { } b ? $" · balance {WalletFormat.Money(b)}" : "");
            rows.Add(new UnreadRow(notification, when(notification), copy, text, "Undo", () => UndoAsync(notification, read)));
        }
        foreach (var notification in unread)
            rows.Add(new UnreadRow(notification, when(notification), copy, null, "Read with AI", ai is null ? null : () => ReadAsync(notification)));
        return rows;
    }

    private async Task ReadAsync(UnreadNotification notification)
    {
        var read = await Task.Run(() => ai!.ReadAsync(notification, CancellationToken.None));
        if (!read.Ok)
        {
            say(read.Problem ?? "AI could not read it.");
            return;
        }
        _done.Insert(0, (notification, read));
        changed();
    }

    private Task UndoAsync(UnreadNotification notification, NotificationRead read)
    {
        ai!.Undo(read, notification);
        _done.RemoveAll(d => ReferenceEquals(d.Read, read));
        changed();
        return Task.CompletedTask;
    }
}
