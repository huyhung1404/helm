using System.Globalization;
using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Modules.Wallet;

/// <summary>
/// The Wallet for AI agents over MCP: the debt book (read it, add an entry), a transaction typed by hand, and what a
/// transaction was (its category and description, after Helm asked). Nothing is
/// ever deleted through here. Dates are the user's local ones: "2026-10-01" or "2026-10-01T18:00".
/// </summary>
/// <param name="funds">The funds, for <c>wallet_funds</c> (left out: no such tool).</param>
public sealed class WalletMcpTools(DebtBook book, WalletStore store, FundBook? funds = null) : IMcpToolProvider
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
            (args, _) => Task.FromResult<object?>(AddDebt(args)))
        {
            Undo = (result, _) => { book.Delete(Id(result)); return Task.CompletedTask; },
        },
        new("wallet_add_transaction", $"Add a transaction to the Wallet by hand: money spent or money in, paid in cash or by a bank Helm does not read. Categories for spending: {Names(CategoryKind.Expense)}. For money in: {Names(CategoryKind.Income)}.",
            McpArgs.Schema(
                ("amount", McpArgs.Text("The amount in đồng, e.g. 45000, 45k or 1tr2."), true),
                ("direction", McpArgs.OneOf("spend: money out; income: money in.", "spend", "income"), true),
                ("description", McpArgs.Text("What it was, e.g. Phở or Lương tháng 11."), true),
                ("category", McpArgs.Text("One of the categories above. Left out: the category this description had before, if any."), false),
                ("when", McpArgs.Text("When it happened, the user's local time: 2026-10-01 or 2026-10-01T12:30. Left out: now."), false)),
            (args, _) => Task.FromResult<object?>(AddTransaction(args)))
        {
            Undo = (result, _) => { store.Delete(Id(result)); return Task.CompletedTask; },
        },
        new("wallet_update_transaction", "Fill in what a transaction already in the Wallet was: its category and/or its description (after Helm asked the user “what was it?”). Only what is given changes.",
            McpArgs.Schema(
                ("id", McpArgs.Text("The transaction's id, as Helm gave it."), true),
                ("category", McpArgs.Text($"One of the categories. For spending: {Names(CategoryKind.Expense)}. For money in: {Names(CategoryKind.Income)}. Between the user's own accounts: {Names(CategoryKind.Transfer)}."), false),
                ("description", McpArgs.Text("What it was, in the user's words, e.g. Ăn trưa với Nam."), false)),
            (args, _) => Task.FromResult<object?>(UpdateTransaction(args)))
        {
            Undo = (result, _) =>
            {
                // Results leave nulls out: no category before is no category_id.
                var before = result.GetProperty("before");
                store.Restore(Id(result), before.TryGetProperty("category_id", out var c) ? c.GetString() : null,
                    before.TryGetProperty("auto", out var auto) && auto.GetBoolean(),
                    before.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "");
                return Task.CompletedTask;
            },
        },
        new("wallet_summary", "What the user spent and received over some days, in đồng, with the spending by category (largest first). Transfers between their own accounts count as neither.",
            McpArgs.Schema(
                ("from", McpArgs.Text("The first day, e.g. 2026-11-01. Left out: the first day of this month."), false),
                ("to", McpArgs.Text("The last day, e.g. 2026-11-30. Left out: today."), false)),
            (args, _) => Task.FromResult<object?>(Summary(args))) { ReadOnly = true },
        .. funds is null ? Array.Empty<McpTool>() :
        [
            new("wallet_funds", "This month's funds (money set aside per category): what each held, spent, has left, and whether it runs low, is used up or over.",
                McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(Funds(funds))) { ReadOnly = true },
        ],
    ];

    private object Funds(FundBook book)
    {
        var zone = TimeZoneInfo.Local;
        var month = WalletFormat.MonthOf(store.Now, zone);
        return book.States(store.Transactions(), month, store.Now, zone).Select(s => new
        {
            fund = s.Fund.Name,
            held = s.Budget,
            spent = s.Spent,
            left = s.Left,
            status = s.Status switch { FundStatus.Low => "low", FundStatus.UsedUp => "used up", FundStatus.Over => "over", _ => "on track" },
            runsOutOn = s.RunsOutOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }).ToList();
    }

    private object Summary(JsonElement args)
    {
        var zone = TimeZoneInfo.Local;
        var today = WalletFormat.LocalDay(store.Now, zone);
        var from = McpArgs.String(args, "from") is { } f ? Day(f) : new DateOnly(today.Year, today.Month, 1);
        var to = McpArgs.String(args, "to") is { } t ? Day(t) : today;
        if (to < from) throw new McpToolException("The last day comes before the first one.");
        var period = WalletStats.Period(store.Transactions(), store.Categories(includeHidden: true), from, to, zone);
        return new
        {
            from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            currency = WalletFormat.Currency,
            spent = period.Spent,
            income = period.Income,
            spending = period.Spending.Select(c => new { category = c.Name, amount = c.Amount, count = c.Count, share = Math.Round(c.Share, 3) }).ToList(),
        };
    }

    private static DateOnly Day(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : throw new McpToolException($"{text} is not a day like 2026-11-01.");

    private static string Id(JsonElement result) =>
        result.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } text ? text : throw new McpToolException("The result has no id.");

    private string Names(CategoryKind kind) =>
        string.Join(", ", store.Categories().Where(c => c.Kind == kind).Select(c => c.Name)) is { Length: > 0 } names ? names : "none yet";

    private object AddTransaction(JsonElement args)
    {
        var amountText = args.TryGetProperty("amount", out var raw) && raw.ValueKind == JsonValueKind.Number
            ? raw.GetDecimal().ToString(CultureInfo.InvariantCulture)
            : McpArgs.RequiredString(args, "amount");
        if (!WalletText.TryParseAmount(amountText.TrimStart('+', '-'), out var value) || value == 0)
            throw new McpToolException($"{amountText} is not an amount. Try 45000, 45.000 or 45k.");
        var amount = McpArgs.RequiredString(args, "direction") switch
        {
            "spend" => -value,
            "income" => value,
            var other => throw new McpToolException($"direction is spend or income, not {other}."),
        };
        var description = McpArgs.RequiredString(args, "description").Trim();
        string? categoryId;
        if (McpArgs.String(args, "category") is { Length: > 0 } wanted)
        {
            var match = store.Categories().FirstOrDefault(c => c.Fits(amount) && string.Equals(c.Name, wanted.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ?? throw new McpToolException($"There is no {(amount < 0 ? "spending" : "income")} category {wanted}. Use one of: {Names(amount < 0 ? CategoryKind.Expense : CategoryKind.Income)}.");
            categoryId = match.Id;
        }
        else
        {
            categoryId = store.SureCategory(description, amount);
        }
        var when = McpArgs.String(args, "when") is { } text ? ParseWhen(text) : store.Now;
        var id = store.AddManual(amount, description, when, categoryId);
        return new
        {
            id,
            amount,
            currency = WalletFormat.Currency,
            description,
            category = categoryId is null ? null : store.Category(categoryId)?.Name,
            when,
        };
    }

    private object UpdateTransaction(JsonElement args)
    {
        var id = McpArgs.RequiredString(args, "id").Trim();
        if (store.Get(id) is not { } transaction) throw new McpToolException($"There is no transaction {id}.");
        string? categoryId = null;
        if (McpArgs.String(args, "category") is { Length: > 0 } wanted)
        {
            var kind = transaction.Amount < 0 ? CategoryKind.Expense : CategoryKind.Income;
            categoryId = (store.Categories().FirstOrDefault(c => c.Fits(transaction.Amount) && string.Equals(c.Name, wanted.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ?? throw new McpToolException($"There is no {(kind == CategoryKind.Expense ? "spending" : "income")} category {wanted}. Use one of: {Names(kind)}, {Names(CategoryKind.Transfer)}.")).Id;
        }
        var description = McpArgs.String(args, "description")?.Trim();
        if (categoryId is null && string.IsNullOrEmpty(description)) throw new McpToolException("Give a category, a description, or both.");
        var before = store.Describe(id, categoryId, description) ?? throw new McpToolException($"There is no transaction {id}.");
        var now = store.Get(id)!;
        return new
        {
            id,
            amount = now.Amount,
            currency = WalletFormat.Currency,
            description = now.Description,
            category = now.CategoryId is { } c ? store.Category(c)?.Name : null,
            before = new { category_id = before.CategoryId, auto = before.AutoCategorized, description = before.Description },
        };
    }

    /// <summary>"2026-10-01" (noon that day, so it stays on that day in any zone) or "2026-10-01T12:30" (that local time).</summary>
    private static DateTimeOffset ParseWhen(string text)
    {
        text = text.Trim();
        var zone = TimeZoneInfo.Local;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), zone.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
        if (!DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new McpToolException($"{text} is not a day or a time like 2026-10-01 or 2026-10-01T12:30.");
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

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
