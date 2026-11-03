namespace Helm.Ai.Auto.Jobs;

/// <summary>
/// O5, on Saturdays: which things in Scratch to keep as a note, make a task of, or let go. Suggestions only: nothing is
/// moved or deleted. Scratch shows only names (a text's first words, never one that looks like a code).
/// </summary>
public sealed class ScratchTidyJob : IAutoJob
{
    public const int FewestItems = 3;

    public string Feature => AiFeatureIds.ScratchTidy;

    public IReadOnlyList<AutoSlot> Slots { get; } = [new("week", AutoEvery.Week, DayOfWeek.Saturday)];

    public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct)
    {
        var items = AutoJson.Items(await context.ReadAsync("scratch_digest", null, ct));
        if (items.Count < FewestItems) return null;
        var text = await context.AskAsync($"""
            This is what lies in Anh's Scratch, a shelf for things in passing (a thing not used for 7 days goes to its trash by
            itself). Suggest, in at most 6 short lines and naming things by their name: which to keep as a note (texts worth
            keeping), which to turn into a task, and which to let go. Only suggest: nothing is moved.
            {AutoJson.Pick(items, "kind", "name", "size", "daysOld", "daysLeft", "canBecomeNote")}
            """, ct).ConfigureAwait(false);
        return new AutoBrief($"Gợi ý dọn Scratch · {items.Count} món", text);
    }
}
