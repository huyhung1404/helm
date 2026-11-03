using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Auto.Jobs;

/// <summary>The pieces of the day the briefs share, cut down from the tools' answers.</summary>
internal static class DayData
{
    /// <summary>The habits to build that are not done today (from habits_list).</summary>
    public static IReadOnlyList<JsonElement> HabitsToDo(JsonElement? habits) =>
        [.. AutoJson.Items(habits, "habits").Where(h => AutoJson.Text(h, "kind") != "quit" && !AutoJson.Flag(h, "doneToday"))];

    /// <summary>A wallet_summary as {spent, income, top categories}.</summary>
    public static string Spending(JsonElement? summary, int top = 5)
    {
        if (summary is null) return "null";
        var categories = new JsonArray();
        foreach (var c in AutoJson.Items(summary, "spending").Take(top))
            categories.Add(new JsonObject { ["category"] = AutoJson.Text(c, "category"), ["amount"] = AutoJson.Number(c, "amount") });
        return new JsonObject
        {
            ["from"] = AutoJson.Text(summary.Value, "from"),
            ["to"] = AutoJson.Text(summary.Value, "to"),
            ["spent"] = AutoJson.Number(summary, "spent"),
            ["income"] = AutoJson.Number(summary, "income"),
            ["top"] = categories,
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
