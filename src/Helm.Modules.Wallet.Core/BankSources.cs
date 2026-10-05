namespace Helm.Modules.Wallet;

/// <summary>A bank whose notifications Helm reads: its own apps, and its name as an SMS sender.</summary>
/// <param name="Id">Stable id, stored in the settings (e.g. "techcombank").</param>
/// <param name="Name">As the pages and transactions show it.</param>
/// <param name="Apps">Android package names of the bank's apps, exactly.</param>
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

    // The SMS apps of the common phones; the phone's default SMS app counts too (see IsMessagingApp).
    private static readonly HashSet<string> s_messagingApps = new(StringComparer.Ordinal)
    {
        "com.google.android.apps.messaging", "com.samsung.android.messaging", "com.android.mms", "com.android.messaging",
        "com.miui.sms", "com.oneplus.mms", "com.coloros.mms", "com.oppo.mms", "com.huawei.message", "com.sonyericsson.conversations",
    };

    /// <summary>
    /// Whether the app shows SMS (its notifications have the sender as the title): one of the common SMS apps, or the
    /// phone's default one. Never guessed from the package name, which any app can choose.
    /// </summary>
    public static bool IsMessagingApp(string app, string? smsApp = null) =>
        s_messagingApps.Contains(app) || !string.IsNullOrEmpty(smsApp) && string.Equals(app, smsApp, StringComparison.Ordinal);

    /// <summary>
    /// The bank a notification is from: one of the bank's own apps, or an SMS app showing a message whose sender is
    /// exactly the bank's name ("ACB", not a contact called "ACB Nam"). Package names are compared exactly, as Android
    /// does: no other app can take the bank's while it is installed, whereas a name merely containing "techcombank" is
    /// anyone's to pick. Null for anything else, so another app cannot pass itself off as a bank.
    /// </summary>
    /// <param name="smsApp">The phone's default SMS app, when the platform knows it.</param>
    public static BankSource? Identify(string app, string title, string? smsApp = null)
    {
        if (string.IsNullOrEmpty(app)) return null;
        foreach (var bank in All)
            if (bank.Apps.Contains(app, StringComparer.Ordinal)) return bank;
        if (!IsMessagingApp(app, smsApp)) return null;
        var sender = WalletText.Fold(WalletText.Clean(title)).Trim();
        foreach (var bank in All)
            if (bank.Senders.Contains(sender, StringComparer.Ordinal)) return bank;
        return null;
    }
}
