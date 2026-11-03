using System.Globalization;
using Helm.Ai.Assistant;

namespace Helm.Modules.Wallet;

/// <summary>
/// Ask Helm opened from "What was it?" (the notification's microphone): the card asks about that one transaction, and
/// what the user says ("ăn trưa với Nam") fills in its category and description with wallet_update_transaction.
/// </summary>
public static class WalletAskContext
{
    public static AskHelmContext For(string transactionId, WalletTransaction transaction, TimeZoneInfo? zone = null)
    {
        var amount = WalletFormat.Signed(transaction.Amount);
        var at = TimeZoneInfo.ConvertTime(transaction.OccurredAt, zone ?? TimeZoneInfo.Local);
        var where = transaction.Bank.Length > 0 ? $", {transaction.Bank}" : "";
        var description = transaction.Description.Length > 0 ? $", “{transaction.Description}”" : "";
        var title = $"{amount}{(transaction.Bank.Length > 0 ? " · " + transaction.Bank : "")}: khoản này là gì?";
        var prompt = $"""
            The user is telling what this transaction in their Wallet was (Helm asked them “Khoản này là gì?”): id {transactionId}, {amount}{where}{description}, {at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.
            Fill it in with wallet_update_transaction for that id: the category that fits, from the tool's list, and a short description in the user's words when they give one (e.g. “Ăn trưa với Nam”). Do not add a new transaction for it.
            """;
        return new AskHelmContext(title, prompt, ["wallet"]);
    }
}
