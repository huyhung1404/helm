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

    /// <summary>
    /// When a bank's balance shows money that moved without a notification (Techcombank says nothing about payments made
    /// in its app), a notification offers to write it down.
    /// </summary>
    public bool AskForMissing { get; set; } = true;

    /// <summary>A transaction with the same description as earlier ones that all went in one category goes there too.</summary>
    public bool AutoCategorize { get; set; } = true;

    /// <summary>Bank notifications Helm could not read (newest first, at most <see cref="WalletCapture.MaxUnread"/>).</summary>
    public List<UnreadNotification> Unread { get; set; } = [];

    /// <summary>What the Android widget's ring shows (see <see cref="WalletWidgetModel"/>).</summary>
    public WalletWidgetChart WidgetChart { get; set; } = WalletWidgetChart.Categories;

    /// <summary>Android widget background (see <see cref="WidgetLook"/>).</summary>
    public WidgetBackground WidgetBackground { get; set; } = WidgetBackground.Card;

    /// <summary>Android widget background opacity, 0 (see-through) to 100 (solid).</summary>
    public int WidgetOpacity { get; set; } = 100;

    public WidgetText WidgetText { get; set; } = WidgetText.Automatic;

    /// <summary>The widget shows dots instead of the balance (the home screen is seen by anyone holding the phone).</summary>
    public bool WidgetHideBalance { get; set; }

    public WidgetLook WidgetLook => new(WidgetBackground, WidgetOpacity, WidgetText);

    public bool IsBankEnabled(string bankId) => !DisabledBanks.Contains(bankId, StringComparer.Ordinal);
}
