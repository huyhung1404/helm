using System.Text.Json.Nodes;
using Helm.Core.Text;

namespace Helm.Ai.Auto.Jobs;

/// <summary>
/// D2: the days to remember in the note “Ngày quan trọng” (birthdays, giỗ in lunar days, inspections, renewals; added
/// by voice with notes_add_important_day): a reminder <see cref="DaysBefore"/> days before and on the day, with a gift
/// idea or a wish to send. No day due: no call at all.
/// </summary>
public sealed class ImportantDaysJob : IAutoJob
{
    public static readonly IReadOnlyList<int> DaysBefore = [3, 0];

    public string Feature => AiFeatureIds.ImportantDays;

    public IReadOnlyList<AutoSlot> Slots { get; } = [new("day", AutoEvery.Day)];

    public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct)
    {
        var today = context.Today;
        var found = AutoJson.Items(await context.ReadAsync("notes_search", new { query = ImportantDays.NoteTitle, limit = 10 }, ct))
            .FirstOrDefault(n => string.Equals(AutoJson.Text(n, "title")?.Trim(), ImportantDays.NoteTitle, StringComparison.CurrentCultureIgnoreCase));
        if (found.ValueKind == System.Text.Json.JsonValueKind.Undefined || AutoJson.Text(found, "id") is not { } id) return null;
        var note = await context.ReadAsync("notes_read", new { id }, ct);
        if (note is null || AutoJson.Text(note.Value, "text") is not { } text) return null;

        var due = new JsonArray();
        foreach (var day in ImportantDays.Parse(text))
        {
            if (day.Next(today) is not { } next) continue;
            var inDays = next.DayNumber - today.DayNumber;
            if (!DaysBefore.Contains(inDays)) continue;
            due.Add(new JsonObject
            {
                ["what"] = day.Label,
                ["date"] = AutoText.Iso(next),
                ["inDays"] = inDays,
                ["lunar"] = day.Lunar ? $"{day.Day:00}/{day.Month:00} âm lịch" : null,
            });
        }
        if (due.Count == 0) return null;

        var answer = await context.AskAsync($"""
            Remind Anh of these days (today is {AutoText.Iso(today)}). For each, one line with what it is and when (today, or in N
            days, with the date; say âm lịch for a lunar one), then one short, concrete suggestion: a gift idea or a wish he
            can send for a birthday, what to prepare for a giỗ, what to have ready for an inspection or a renewal. At most 8
            short lines.
            {due.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}
            """, ct).ConfigureAwait(false);
        var first = due[0]!;
        var title = due.Count == 1
            ? (first["inDays"]!.GetValue<int>() == 0 ? $"Hôm nay: {first["what"]}" : $"Còn {first["inDays"]} ngày: {first["what"]}")
            : $"Ngày quan trọng · {due.Count} ngày sắp tới";
        return new AutoBrief(title, answer);
    }
}
