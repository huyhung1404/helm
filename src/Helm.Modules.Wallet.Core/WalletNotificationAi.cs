using System.Text.Json;
using System.Text.RegularExpressions;
using Helm.Ai;
using Helm.Core.Ai;

namespace Helm.Modules.Wallet;

/// <summary>What reading a notification with AI came to: the transaction saved (or found saved), or why not.</summary>
public sealed record NotificationRead(string? TransactionId, WalletTransaction? Transaction, string? Problem, bool Duplicate = false)
{
    public bool Ok => TransactionId is not null;
}

/// <summary>
/// "Read with AI" on a bank notification the parser missed, only when the user asks: that one notification's text
/// goes to AI (<see cref="AiFeatureIds.WalletReadNotification"/>, a PC agent first, the API as Ask Helm's route
/// allows), which answers the amount, the direction, the counterpart, the description and the balance as JSON. The
/// answer is checked against the text (the amount and the balance must be in it, a sign in it must agree) before the
/// transaction is saved the usual way; <see cref="Undo"/> takes it back and the notification returns to the list.
/// </summary>
public sealed partial class WalletNotificationAi(WalletStore store, WalletCapture capture, IAiText ai)
{
    internal const string System = """
        You read one bank notification from a Vietnamese bank and find the transaction in it.
        Reply with ONLY one JSON object and nothing else:
        {"amount":65000,"direction":"out","counterpart":"NGUYEN VAN A","description":"what the bank says it was for","balance":1234567,"account":"1903xxxx0123"}
        - amount: the transaction's amount in đồng, a positive number, exactly as written in the text.
        - direction: "out" for money that left the account, "in" for money that came in.
        - counterpart, description, account: as written in the text; null when the text does not say.
        - balance: the balance after the transaction, when the text gives it; else null.
        When the text is not a transaction (an advert, a reminder, a code), reply {"transaction":null}.
        """;

    public async Task<NotificationRead> ReadAsync(UnreadNotification notification, CancellationToken ct)
    {
        // A one-time code never leaves the phone.
        if (BankNotificationParser.HasOneTimeCode(notification.Title, notification.Text))
            return new NotificationRead(null, null, "This looks like a one-time code: Helm never sends those anywhere.");
        var prompt = $"Bank: {notification.Bank}\nTitle: {notification.Title}\nText: {notification.Text}";
        var answer = await ai.AskAsync(new AiTextRequest(AiFeatureIds.WalletReadNotification, System, prompt), ct);
        if (!answer.Ok) return new NotificationRead(null, null, answer.Problem ?? "AI did not answer.");
        if (Extract(answer.Text, notification) is not { } parsed) return new NotificationRead(null, null, "AI found no transaction it could show in this notification.");
        var saved = store.AddCaptured(parsed, capture.Settings.Current.AutoCategorize);
        capture.ForgetUnread(notification);
        return new NotificationRead(saved.Id, saved.Transaction, null, saved.Kind == CaptureKind.Duplicate);
    }

    /// <summary>Takes back what <see cref="ReadAsync"/> saved (not one that was saved before) and lists the notification again.</summary>
    public void Undo(NotificationRead read, UnreadNotification notification)
    {
        if (read.TransactionId is { } id && !read.Duplicate) store.Delete(id);
        capture.KeepUnread(notification);
    }

    /// <summary>
    /// AI's answer as a transaction, or null when it does not hold up against the text: an amount that is not in it,
    /// a direction its sign denies. A balance or an account that is not in the text is dropped.
    /// </summary>
    internal static ParsedTransaction? Extract(string answer, UnreadNotification notification)
    {
        if (AiJson.Object(answer) is not { } json) return null;
        var folded = WalletText.Fold(WalletText.Clean(notification.Title + "\n" + notification.Text));
        if (Number(json, "amount") is not { } amount || amount <= 0 || amount > WalletLimits.MaxAmount) return null;
        var signs = SignsOf(folded, amount);
        if (signs is null) return null; // not in the text
        var direction = Text(json, "direction")?.ToLowerInvariant();
        var outgoing = direction switch
        {
            "out" or "debit" or "spend" => true,
            "in" or "credit" or "income" => false,
            _ => (bool?)null,
        };
        if (outgoing is null || outgoing.Value && signs.Contains('+') || !outgoing.Value && signs.Contains('-')) return null;
        var balance = Number(json, "balance") is { } b && SignsOf(folded, b) is not null ? b : (decimal?)null;
        var account = Text(json, "account") is { } a && folded.Contains(WalletText.Fold(a), StringComparison.Ordinal) ? a : "";
        var description = Text(json, "description") ?? "";
        if (Text(json, "counterpart") is { } who && !WalletText.Fold(description).Contains(WalletText.Fold(who), StringComparison.Ordinal))
            description = description.Length > 0 ? $"{who} {description}" : who;
        var original = notification.Title.Length > 0 ? notification.Title + "\n" + notification.Text : notification.Text;
        return new ParsedTransaction(notification.Bank, WalletLimits.Clip(account, 40), outgoing.Value ? -amount : amount, balance,
            WalletLimits.Clip(description, WalletLimits.Description), notification.At, original);
    }

    /// <summary>The signs written right before <paramref name="value"/> each time it is in the text (' ' for none); null when it is not.</summary>
    private static List<char>? SignsOf(string folded, decimal value)
    {
        List<char>? signs = null;
        foreach (Match m in Numbers().Matches(folded))
        {
            if (!WalletText.TryParseNumber(m.Groups[2].Value, out var n) || n != value) continue;
            (signs ??= []).Add(m.Groups[1].Value is { Length: > 0 } sign ? sign[0] : ' ');
        }
        return signs;
    }

    private static decimal? Number(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return Math.Abs(number);
        return value.ValueKind == JsonValueKind.String && WalletText.TryParseNumber(value.GetString()!.Trim().TrimStart('+', '-'), out var parsed)
            ? parsed : null;
    }

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
            ? text : null;

    // A sign right before the number (not inside a word or a reference), then the number as banks write it.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])([+-]?)\s?(\d{1,3}(?:[.,]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Numbers();
}
