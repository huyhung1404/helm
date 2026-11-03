using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Modules.Wallet;

/// <summary>
/// Money shared with other people, for AI agents (Ask Helm first): a bill split among friends in one call (the user's
/// share as spending, a debt per person who still owes), and the message that reminds someone of a debt.
/// </summary>
public sealed class WalletShareTools(DebtBook book, WalletStore store) : IMcpToolProvider
{
    public string? ModuleId => WalletIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("wallet_split_bill",
            "Split a shared bill evenly, all in one call: when the user paid, their own share is saved as spending and each person who still owes gets a debt; those who already paid their share are saved as settled. When someone else paid, the user owes that person their share. Each record comes back with its own summary.",
            McpArgs.Schema(
                ("total", McpArgs.Text("The whole bill in đồng: 1200000, 1tr2 or 1.2tr."), true),
                ("description", McpArgs.Text("What it was, e.g. Ăn lẩu."), true),
                ("people", McpArgs.Number("How many people share it, the user included (\"chia 4\" is 4). Left out: the people named plus the user."), false),
                ("owing", McpArgs.List("The people who have not paid the user their share yet."), false),
                ("paid", McpArgs.List("The people who already paid the user their share."), false),
                ("payer", McpArgs.Text("Who paid the bill, when it was not the user."), false),
                ("category", McpArgs.Text("The user's share's spending category (as wallet_add_transaction takes it). Left out: the one this description had before."), false),
                ("when", McpArgs.Text("When, the user's local time: 2026-10-01 or 2026-10-01T19:30. Left out: now."), false)),
            (args, _) => Task.FromResult<object?>(Split(args)))
        {
            Undo = (record, _) => { McpTool.Undone(TakeBack(record)); return Task.CompletedTask; },
            Brief = "Split a bill evenly in one call. The user paid: their share is saved as spending, each of owing gets a debt, each of paid is saved settled. Someone else paid (payer): the user owes them their share.",
            BriefArgs = new Dictionary<string, string>
            {
                ["total"] = "đồng: 1200000, 1tr2",
                ["people"] = "how many share it, the user included (chia 4: 4); default the people named + 1",
                ["category"] = "the user's share's, as wallet_add_transaction takes it; default this description's last one",
                ["when"] = "local: 2026-10-01T19:30; default now",
            },
            // The card lists each record with its own summary: the model needs no ids.
            BriefResult = McpTool.Drop("total", "people", "currency", "kind", "id", "ids"),
        },
        new("wallet_debt_reminder",
            "A polite Vietnamese message reminding someone of what they owe the user, from the debt book: the amount, what for and since when. Show it to the user and offer to share it (phone_share_text); Helm never sends it itself.",
            McpArgs.Schema(
                ("person", McpArgs.Text("Who, as the user said it."), true),
                ("amount", McpArgs.Text("The amount to ask for, when not the whole balance: 200k."), false)),
            (args, _) => Task.FromResult<object?>(Reminder(args)))
        {
            ReadOnly = true,
            Brief = "A polite Vietnamese message reminding someone of what they owe the user. Show it and offer phone_share_text; Helm never sends it.",
            BriefArgs = new Dictionary<string, string> { ["amount"] = "only when not the whole balance: 200k" },
            BriefResult = McpTool.Drop("currency"),
        },
    ];

    private object Split(JsonElement args)
    {
        var total = WalletArgs.RequiredAmount(args, "total");
        var description = McpArgs.RequiredString(args, "description").Trim();
        var owing = Names(McpArgs.Strings(args, "owing"));
        var paid = Names(McpArgs.Strings(args, "paid")).Where(p => !owing.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        var payer = McpArgs.String(args, "payer") is { } who && !IsUser(who) ? DebtLedger.Clean(who) : null;
        var named = owing.Count + paid.Count + (payer is not null && !owing.Concat(paid).Any(n => DebtLedger.Key(n) == DebtLedger.Key(payer)) ? 1 : 0);
        var people = McpArgs.Int(args, "people") ?? named + 1;
        if (people < 2) throw new McpToolException("A split needs at least two people, the user included.");
        if (people < named + 1) throw new McpToolException($"{named} people are named besides the user, but the bill is split {people} ways.");
        var (each, mine) = BillSplit.Shares(total, people);
        var when = McpArgs.String(args, "when") is { } text ? WalletArgs.When(text, TimeZoneInfo.Local) : store.Now;

        var records = new List<object>();
        try
        {
            if (payer is not null)
            {
                // The others settle with whoever paid: only what the user owes is the user's.
                var id = book.Add(payer, mine, DebtEntryKind.IOwe, description);
                records.Add(new { summary = $"Anh nợ {payer} {WalletSpeech.Money(mine)} · {description}", kind = "debts", ids = new[] { id } });
            }
            else
            {
                var categoryId = Category(args, description, -mine);
                var tx = store.AddManual(-mine, description, when, categoryId);
                var category = categoryId is null ? null : store.Category(categoryId)?.Name;
                records.Add(new { summary = $"Chi {WalletSpeech.Money(mine)} · {description} (phần Anh){(category is null ? "" : " · " + category)}", kind = "transaction", id = tx });
                foreach (var person in owing)
                {
                    var id = book.Add(person, each, DebtEntryKind.OwesMe, description);
                    records.Add(new { summary = $"{person} nợ {WalletSpeech.Money(each)} · {description}", kind = "debts", ids = new[] { id } });
                }
                foreach (var person in paid)
                {
                    // Owed and paid back at once: the book keeps it, settled.
                    var owed = book.Add(person, each, DebtEntryKind.OwesMe, description);
                    var back = book.Add(person, each, DebtEntryKind.Repayment, description);
                    records.Add(new { summary = $"{person} đã trả {WalletSpeech.Money(each)} · {description}", kind = "debts", ids = new[] { owed, back } });
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // All of it or none: what was saved before the problem goes again.
            foreach (var record in records) TakeBack(JsonSerializer.SerializeToElement(record));
            throw new McpToolException(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }
        return new { total, people, each, mine, currency = WalletFormat.Currency, records };
    }

    /// <summary>One record of a split: its transaction, or its debt entries. False when none of it was there.</summary>
    private bool TakeBack(JsonElement record)
    {
        if (record.TryGetProperty("kind", out var kind) && kind.GetString() == "transaction") return store.Delete(WalletArgs.Id(record));
        var any = false;
        if (record.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            foreach (var id in ids.EnumerateArray().Reverse()) any |= book.Delete(id.GetString()!);
        return any;
    }

    private string? Category(JsonElement args, string description, decimal amount)
    {
        if (McpArgs.String(args, "category") is not { Length: > 0 } wanted) return store.SureCategory(description, amount);
        return store.Categories().FirstOrDefault(c => c.Fits(amount) && string.Equals(c.Name, wanted.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id
            ?? throw new McpToolException($"There is no spending category {wanted}. Use one of: {string.Join(", ", store.Categories().Where(c => c.Kind == CategoryKind.Expense).Select(c => c.Name))}.");
    }

    private object Reminder(JsonElement args)
    {
        var name = McpArgs.RequiredString(args, "person");
        var open = book.Open().Where(p => p.Balance > 0).ToList();
        var person = DebtReminder.Find(open, name) ?? throw new McpToolException(open.Count == 0
            ? "Nobody owes the user anything in the debt book."
            : $"{name} owes the user nothing in the debt book. People who owe: {string.Join(", ", open.Select(p => $"{p.Name} ({WalletSpeech.Money(p.Balance)})"))}.");
        var amount = WalletArgs.Amount(args, "amount");
        var zone = TimeZoneInfo.Local;
        return new
        {
            person = person.Name,
            balance = person.Balance,
            currency = WalletFormat.Currency,
            message = DebtReminder.Message(person, amount, book.Now, zone),
            due = person.Due(zone),
        };
    }

    private static List<string> Names(IEnumerable<string> names) =>
        [.. names.Select(DebtLedger.Clean).Where(n => n.Length > 0 && !IsUser(n)).DistinctBy(DebtLedger.Key)];

    /// <summary>The user themself, as a model may write them in a list of people.</summary>
    private static bool IsUser(string name) => DebtLedger.Key(name) is "me" or "user" or "the user" or "anh" or "tôi" or "toi" or "mình" or "minh";
}
