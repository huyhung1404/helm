using System.Globalization;
using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Modules.Wallet;

/// <summary>
/// Savings goals for AI agents: list them (how far each is, the money available), start one, and set money aside
/// ("để dành 200k cho Mini PC"). Money set aside leaves the balance but is not spending.
/// </summary>
public sealed class GoalMcpTools(GoalBook goals) : IMcpToolProvider
{
    public string? ModuleId => WalletIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("wallet_goals", "The user's savings goals (things they save up for, like a mini PC): price, money set aside, what is missing, the date they want it by and what to set aside a week for it; plus the money available to set aside (the balance after what the goals hold).",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(List()))
        {
            ReadOnly = true,
            Brief = "Savings goals: price, set aside, missing, by when, a week's share; the money available.",
            BriefResult = McpTool.Drop("currency", "created", "ended"),
        },
        new("wallet_goal_add", "Start saving for something the user wants to buy. Its photo is added in the app.",
            McpArgs.Schema(
                ("name", McpArgs.Text("What it is: Mini PC, Bàn phím cơ."), true),
                ("price", McpArgs.Text("What it costs, in đồng (12tr, 12990000)."), true),
                ("by", McpArgs.Text("The date they want it by (2027-01-31); optional."), false),
                ("start", McpArgs.Text("Money already saved for it, set aside now, in đồng; optional."), false)),
            (args, _) => Task.FromResult<object?>(Add(args)))
        {
            Undo = (result, _) =>
            {
                var id = WalletArgs.Id(result);
                if (goals.Get(id) is { IsSaving: true }) goals.GiveUp(id);
                McpTool.Undone(goals.Delete(id));
                return Task.CompletedTask;
            },
            Brief = "Start a savings goal (name, price; optional date and money already saved).",
            BriefArgs = new Dictionary<string, string> { ["price"] = "đồng", ["by"] = "2027-01-31", ["start"] = "đồng set aside now" },
            BriefResult = McpTool.Keep("id", "name", "price", "saved"),
        },
        new("wallet_goal_save", "Set money aside for a savings goal (\"để dành 200k cho Mini PC\"): it leaves the balance now but is not spending, and comes back if the user gives the goal up. Never more than the goal still misses, nor more than is available.",
            McpArgs.Schema(
                ("goal", McpArgs.Text("The goal's name or id (from wallet_goals)."), true),
                ("amount", McpArgs.Text("How much, in đồng (200k)."), true)),
            (args, _) => Task.FromResult<object?>(Save(args)))
        {
            Undo = (result, _) =>
            {
                var entry = new GoalEntry { At = result.GetProperty("at").GetDateTimeOffset(), Amount = result.GetProperty("amount").GetDecimal() };
                McpTool.Undone(goals.Remove(WalletArgs.Id(result), entry));
                return Task.CompletedTask;
            },
            Brief = "Set money aside for a goal (\"để dành 200k cho Mini PC\"); off the balance, not spending.",
            BriefArgs = new Dictionary<string, string> { ["goal"] = "its name or id", ["amount"] = "đồng" },
            BriefResult = McpTool.Keep("name", "amount", "saved", "left", "percent"),
        },
    ];

    private object List()
    {
        var zone = TimeZoneInfo.Local;
        var today = WalletFormat.LocalDay(goals.Wallet.Now, zone);
        return new
        {
            available = goals.Available(zone),
            set_aside = goals.SetAside(),
            currency = WalletFormat.Currency,
            goals = goals.All().Select(g => new
            {
                id = g.Id,
                name = g.Value.Name,
                state = g.Value.State switch { GoalState.Bought => "bought", GoalState.GaveUp => "given up", _ => "saving" },
                price = g.Value.Price,
                saved = g.Value.Saved,
                left = g.Value.Left,
                percent = GoalMath.Percent(g.Value),
                by = g.Value.Due is { } due ? Day(due) : null,
                per_week = g.Value.IsSaving ? GoalMath.PerWeek(g.Value, today) : null,
                paid = g.Value.Paid,
                created = g.Value.CreatedAt,
                ended = g.Value.EndedAt,
            }).ToList(),
        };
    }

    private object Add(JsonElement args)
    {
        var name = McpArgs.RequiredString(args, "name");
        var price = WalletArgs.RequiredAmount(args, "price");
        DateOnly? due = null;
        if (McpArgs.String(args, "by") is { Length: > 0 } text)
            due = DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day : throw new McpToolException("by must be a date like 2027-01-31.");
        try
        {
            var id = goals.Add(name, price, due, WalletArgs.Amount(args, "start") ?? 0, TimeZoneInfo.Local);
            var goal = goals.Get(id)!;
            return new { id, name = goal.Name, price = goal.Price, saved = goal.Saved, by = goal.Due is { } d ? Day(d) : null };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new McpToolException(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }
    }

    private object Save(JsonElement args)
    {
        var wanted = McpArgs.RequiredString(args, "goal");
        var goal = goals.Find(wanted) ?? throw new McpToolException(goals.Saving() is { Count: > 0 } all
            ? $"There is no single goal {wanted}. Goals: {string.Join(", ", all.Select(g => g.Value.Name))}."
            : "There are no savings goals yet: start one with wallet_goal_add.");
        try
        {
            var entry = goals.Put(goal.Id, WalletArgs.RequiredAmount(args, "amount"), TimeZoneInfo.Local);
            var now = goals.Get(goal.Id)!;
            return new
            {
                id = goal.Id,
                name = now.Name,
                amount = entry.Amount,
                at = entry.At,
                saved = now.Saved,
                left = now.Left,
                percent = GoalMath.Percent(now),
                available = goals.Available(TimeZoneInfo.Local),
                currency = WalletFormat.Currency,
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new McpToolException(ex.Message);
        }
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
