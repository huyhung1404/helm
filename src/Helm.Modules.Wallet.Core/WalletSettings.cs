using Helm.Core.Settings;

namespace Helm.Modules.Wallet;

/// <summary>
/// Device-local preferences (the transactions, categories and budget are synced through <see cref="WalletStore"/>).
/// Stored in settings/wallet.json on both apps; the notification options only matter on the phone.
/// </summary>
public sealed class WalletSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Read the banks' notifications and save their transactions (Android, with notification access).</summary>
    public bool ListenEnabled { get; set; } = true;

    /// <summary>Banks (<see cref="BankSource.Id"/>) whose notifications are left alone.</summary>
    public List<string> DisabledBanks { get; set; } = [];

    /// <summary>After a payment, a notification with a few categories to pick from.</summary>
    public bool AskForCategory { get; set; } = true;

    /// <summary>A transaction with the same description as earlier ones that all went in one category goes there too.</summary>
    public bool AutoCategorize { get; set; } = true;

    /// <summary>Bank notifications Helm could not read (newest first, at most <see cref="WalletCapture.MaxUnread"/>).</summary>
    public List<UnreadNotification> Unread { get; set; } = [];

    public bool IsBankEnabled(string bankId) => !DisabledBanks.Contains(bankId, StringComparer.Ordinal);
}
