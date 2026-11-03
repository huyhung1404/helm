using System.Globalization;
using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Modules.Wallet;

/// <summary>
/// Recurring bills for AI agents (Ask Helm first): list them, add one, and save a payment (a Wallet transaction, and
/// that month marked paid). The Tracker's daily reminder tells the user before each comes due.
/// </summary>
public sealed class BillMcpTools(BillBook bills, WalletStore store) : IMcpToolProvider
{
    public string? ModuleId => WalletIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("bills_list", "The user's recurring bills (electricity, water, internet, rent…): the amount or what it came to last time, the day of the month it is due, the next unpaid due day, and whether this month is paid.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(List()))
        {
            ReadOnly = true,
            Brief = "Recurring bills: amount (or last time's), due day of the month, next unpaid due day, paid this month.",
            BriefResult = McpTool.Drop("currency", "varies", "last_paid"),
        },
        new("bill_add", "Add a bill that comes every month; Helm reminds the user before it is due.",
            McpArgs.Schema(
                ("name", McpArgs.Text("What it is: Tiền điện, Internet, Tiền nhà."), true),
                ("due_day", McpArgs.Number("The day of the month it is due (1-31)."), true),
                ("amount", McpArgs.Text("The same amount every month, in đồng (250k). Left out: it changes every month."), false),
                ("category", McpArgs.Text("Its spending category (optional)."), false)),
            (args, _) => Task.FromResult<object?>(Add(args)))
        {
            Undo = (result, _) => { McpTool.Undone(bills.Delete(WalletArgs.Id(result))); return Task.CompletedTask; },
            Brief = "Add a monthly bill; Helm reminds the user before it is due.",
            BriefArgs = new Dictionary<string, string> { ["due_day"] = "1-31", ["amount"] = "the same every month, đồng; left out: it varies" },
            BriefResult = McpTool.Keep("name", "amount", "next_due"),
        },
        new("bill_pay", "The user paid a bill (\"đóng tiền điện rồi 450k\"): saves the spending in the Wallet and marks that month paid.",
            McpArgs.Schema(
                ("bill", McpArgs.Text("The bill's name or id (from bills_list)."), true),
                ("amount", McpArgs.Text("What it came to, in đồng. Left out: the bill's fixed amount."), false),
                ("when", McpArgs.Text("When it was paid, the user's local time: 2026-11-03 or 2026-11-03T09:00. Left out: now."), false)),
            (args, _) => Task.FromResult<object?>(Pay(args)))
        {
            Brief = "The user paid a bill (\"đóng tiền điện rồi 450k\"): saves the spending and marks the month paid.",
            BriefArgs = new Dictionary<string, string>
            {
                ["bill"] = "its name or id",
                ["amount"] = "đồng; default the bill's fixed amount",
                ["when"] = "local: 2026-11-03T09:00; default now",
            },
            BriefResult = McpTool.Keep("name", "amount", "month", "next_due"),
            Undo = (result, _) =>
            {
                var payment = new BillPayment(WalletArgs.Id(result, "transaction_id"), DateOnly.ParseExact(WalletArgs.Id(result, "month") + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    result.GetProperty("amount").GetDecimal(), Decimal(result, "previous_last_amount"),
                    result.TryGetProperty("previous_last_paid_at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTimeOffset() : null);
                bills.Unpay(WalletArgs.Id(result), payment);
                return Task.CompletedTask;
            },
        },
    ];

    private object List()
    {
        var today = WalletFormat.LocalDay(bills.Now, TimeZoneInfo.Local);
        var month = new DateOnly(today.Year, today.Month, 1);
        return bills.All().Select(b => new
        {
            id = b.Id,
            name = b.Value.Name,
            amount = b.Value.Amount,
            varies = b.Value.Amount is null,
            last_amount = b.Value.LastAmount,
            last_paid = b.Value.LastPaidAt,
            currency = WalletFormat.Currency,
            due_day = b.Value.DueDay,
            next_due = Day(b.Value.NextDue()),
            days_left = b.Value.NextDue().DayNumber - today.DayNumber,
            paid_this_month = b.Value.PaidMonths.Contains(month),
        }).ToList();
    }

    private object Add(JsonElement args)
    {
        var name = McpArgs.RequiredString(args, "name");
        var day = McpArgs.Int(args, "due_day") ?? throw new McpToolException("due_day is required.");
        var amount = McpArgs.String(args, "amount") is { } text && IsVaries(text) ? null : WalletArgs.Amount(args, "amount");
        string? categoryId = null;
        if (McpArgs.String(args, "category") is { Length: > 0 } wanted)
            categoryId = store.Categories().FirstOrDefault(c => c.Kind == CategoryKind.Expense && string.Equals(c.Name, wanted.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id
                ?? throw new McpToolException($"There is no spending category {wanted}.");
        try
        {
            var id = bills.Add(name, amount, day, categoryId);
            var bill = bills.Get(id)!;
            return new { id, name = bill.Name, amount, due_day = day, next_due = Day(bill.NextDue()) };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new McpToolException(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }
    }

    private object Pay(JsonElement args)
    {
        var wanted = McpArgs.RequiredString(args, "bill");
        var bill = (bills.Get(wanted) is not null ? bills.All().FirstOrDefault(b => b.Id == wanted) : bills.Find(wanted))
            ?? throw new McpToolException(bills.All() is { Count: > 0 } all
                ? $"There is no single bill {wanted}. Bills: {string.Join(", ", all.Select(b => b.Value.Name))}."
                : "There are no bills yet: add it with bill_add first.");
        var when = McpArgs.String(args, "when") is { } text ? WalletArgs.When(text, TimeZoneInfo.Local) : (DateTimeOffset?)null;
        try
        {
            var payment = bills.Pay(bill.Id, WalletArgs.Amount(args, "amount"), when);
            var next = bills.Get(bill.Id)!.NextDue();
            return new
            {
                id = bill.Id,
                name = bill.Value.Name,
                transaction_id = payment.TransactionId,
                month = payment.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                amount = payment.Amount,
                currency = WalletFormat.Currency,
                next_due = Day(next),
                previous_last_amount = payment.PreviousLastAmount,
                previous_last_paid_at = payment.PreviousLastPaidAt,
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new McpToolException(ex.Message);
        }
    }

    private static bool IsVaries(string text) => Helm.Core.Text.TextSearch.Fold(text.Trim()) is "varies" or "thay doi" or "khong co dinh" or "tuy thang";

    private static decimal? Decimal(JsonElement result, string name) =>
        result.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
