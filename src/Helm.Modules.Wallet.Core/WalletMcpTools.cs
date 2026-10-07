using System.Globalization;
using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Modules.Wallet;

/// <summary>
/// The Wallet's debt book for AI agents over MCP: read it, add an entry. Nothing is ever deleted through here. Dates are
/// the user's local ones: "2026-10-01" or "2026-10-01T18:00".
/// </summary>
public sealed class WalletMcpTools(DebtBook book) : IMcpToolProvider
{
    public string? ModuleId => WalletIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("wallet_debts", "The debt book in the user's Wallet: each person's balance in đồng (positive: they owe the user; negative: the user owes them), due date and entries.",
            McpArgs.Schema(("include_settled", McpArgs.Flag("Also people whose balance is back to 0."), false)),
            (args, _) => Task.FromResult<object?>(Debts(McpArgs.Bool(args, "include_settled") == true))) { ReadOnly = true },
        new("wallet_add_debt", "Add an entry to the debt book: someone owes the user, the user owes someone, or a repayment (brings the balance back toward 0).",
            McpArgs.Schema(
                ("person", McpArgs.Text("The person's name (the same name adds to their balance)."), true),
                ("amount", McpArgs.Text("The amount in đồng, e.g. 150000, 150.000, 150k or 1.5tr."), true),
                ("kind", McpArgs.OneOf("Which way the money goes.", "owes_me", "i_owe", "repayment"), true),
                ("note", McpArgs.Text("What it was for."), false),
                ("due", McpArgs.Text("When it should be paid back: 2026-10-01 or 2026-10-01T18:00."), false)),
            (args, _) => Task.FromResult<object?>(AddDebt(args))),
    ];

    private object Debts(bool includeSettled)
    {
        var zone = TimeZoneInfo.Local;
        var people = book.Open().Where(p => includeSettled || !p.IsSettled);
        if (includeSettled) people = people.Concat(book.Settled());
        return people.Select(p => new
        {
            person = p.Name,
            balance = p.Balance,
            currency = WalletFormat.Currency,
            meaning = p.Balance > 0 ? "owes the user" : p.Balance < 0 ? "the user owes them" : "settled",
            settled = p.SettledAt,
            due = p.Due(zone),
            entries = p.Entries.Select(e => new
            {
                when = e.Debt.CreatedAt,
                kind = e.Kind switch { DebtEntryKind.OwesMe => "owes_me", DebtEntryKind.IOwe => "i_owe", _ => "repayment" },
                amount = e.Debt.Amount,
                note = e.Debt.Note.Length > 0 ? e.Debt.Note : null,
                balance_after = e.BalanceAfter,
                from_transaction = e.Debt.TransactionId is not null ? true : (bool?)null,
            }).ToList(),
        }).ToList();
    }

    private object AddDebt(JsonElement args)
    {
        var person = McpArgs.RequiredString(args, "person").Trim();
        var amountText = args.TryGetProperty("amount", out var raw) && raw.ValueKind == JsonValueKind.Number
            ? raw.GetDecimal().ToString(CultureInfo.InvariantCulture)
            : McpArgs.RequiredString(args, "amount");
        if (!WalletText.TryParseAmount(amountText, out var amount)) throw new McpToolException($"{amountText} is not an amount. Try 150000, 150.000 or 150k.");
        var kind = McpArgs.RequiredString(args, "kind") switch
        {
            "owes_me" => DebtEntryKind.OwesMe,
            "i_owe" => DebtEntryKind.IOwe,
            "repayment" => DebtEntryKind.Repayment,
            var other => throw new McpToolException($"kind is owes_me, i_owe or repayment, not {other}."),
        };
        var dueAt = McpArgs.String(args, "due") is { } due ? ParseDue(due) : null;
        try
        {
            var id = book.Add(person, amount, kind, McpArgs.String(args, "note") ?? "", dueAt);
            return new { id, person = DebtLedger.Clean(person), balance = book.Balance(person), currency = WalletFormat.Currency };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new McpToolException(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }
    }

    /// <summary>"2026-10-01" (the end of that day) or "2026-10-01T18:00" (at that local time).</summary>
    private static DateTimeOffset? ParseDue(string text)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return DebtDue.FromLocal(day.ToDateTime(TimeOnly.MinValue), null, TimeZoneInfo.Local);
        if (!DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new McpToolException($"{text} is not a day or a time like 2026-10-01 or 2026-10-01T18:00.");
        return DebtDue.FromLocal(local.Date, local.TimeOfDay, TimeZoneInfo.Local);
    }
}
