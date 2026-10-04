namespace Helm.Modules.Wallet;

/// <summary>A bank whose notifications Helm reads: its own apps, and its name as an SMS sender.</summary>
/// <param name="Id">Stable id, stored in the settings (e.g. "techcombank").</param>
/// <param name="Name">As the pages and transactions show it.</param>
/// <param name="Apps">Android package names of the bank's apps.</param>
/// <param name="Senders">The SMS sender names, folded (see <see cref="WalletText.Fold"/>).</param>
public sealed record BankSource(string Id, string Name, IReadOnlyList<string> Apps, IReadOnlyList<string> Senders);

/// <summary>The banks Helm listens to, and how a notification is matched to one.</summary>
public static class BankSources
{
    public static BankSource Techcombank { get; } = new("techcombank", "Techcombank",
        ["vn.com.techcombank.bb.app", "com.techcombank.bb.app"], ["techcombank", "tcb"]);

    public static BankSource Acb { get; } = new("acb", "ACB",
        ["mobile.acb.com.vn", "vn.com.acb.acbone"], ["acb"]);

    public static IReadOnlyList<BankSource> All { get; } = [Techcombank, Acb];

    public static BankSource? Get(string id) => All.FirstOrDefault(b => b.Id == id);

    // SMS apps of the common phones; others are recognised by their package name (see IsMessagingApp).
    private static readonly HashSet<string> s_messagingApps = new(StringComparer.Ordinal)
    {
        "com.google.android.apps.messaging", "com.samsung.android.messaging", "com.android.mms", "com.android.messaging",
        "com.miui.sms", "com.oneplus.mms", "com.coloros.mms", "com.oppo.mms", "com.huawei.message", "com.sonyericsson.conversations",
    };

    /// <summary>Whether the app shows SMS (its notifications have the sender as the title).</summary>
    public static bool IsMessagingApp(string app)
    {
        var a = app.ToLowerInvariant();
        return s_messagingApps.Contains(a) || a.Contains(".mms", StringComparison.Ordinal) || a.Contains("messaging", StringComparison.Ordinal)
            || a.Split('.').Any(s => s is "sms" or "messages" or "message");
    }

    /// <summary>
    /// The bank a notification is from: one of the bank's apps (or another app of the bank, by its package name), or an
    /// SMS whose sender is the bank. Null for anything else.
    /// </summary>
    public static BankSource? Identify(string app, string title)
    {
        var a = (app ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0) return null;
        foreach (var bank in All)
            if (bank.Apps.Contains(a)) return bank;
        var segments = a.Split('.');
        if (a.Contains("techcombank", StringComparison.Ordinal)) return Techcombank;
        if (segments.Any(s => s == "acb" || s.StartsWith("acbone", StringComparison.Ordinal))) return Acb;
        if (!IsMessagingApp(a)) return null;
        var sender = WalletText.Fold(WalletText.Clean(title)).Trim();
        foreach (var bank in All)
            if (bank.Senders.Any(s => sender == s || sender.StartsWith(s + " ", StringComparison.Ordinal) || sender.StartsWith(s + ":", StringComparison.Ordinal)))
                return bank;
        return null;
    }
}
