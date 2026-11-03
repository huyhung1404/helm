using System.Text.Json;
using Helm.Ai.Assistant;

namespace Helm.Ai.Auto.Jobs;

/// <summary>
/// O2, every hour: new notes the user wrote without a title get one, and a few tags, in one call for the batch. Each is
/// an edit Helm can undo (the brief lists them; "hoàn tác" in Ask Helm takes them back). A note is looked at once:
/// one the user is still typing (edited in the last minutes) waits for the next hour.
/// </summary>
public sealed class NoteTitlesJob : IAutoJob
{
    public const int MostNotes = 8;
    private const int ShownLength = 600;
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan New = TimeSpan.FromDays(3);

    public string Feature => AiFeatureIds.NoteTitles;

    public IReadOnlyList<AutoSlot> Slots { get; } = [new("hour", AutoEvery.Hour)];

    public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct)
    {
        var now = new DateTimeOffset(context.Now, TimeZoneInfo.Local.GetUtcOffset(context.Now));
        var untitled = AutoJson.Items(await context.ReadAsync("notes_search", new { untitled = true, limit = 50 }, ct))
            .Where(n => AutoJson.Text(n, "id") is { } id && !context.Remembers(id))
            .Where(n => DateTimeOffset.TryParse(AutoJson.Text(n, "edited"), out var edited) && now - edited > Settled && now - edited < New)
            .Take(MostNotes).ToList();
        if (untitled.Count == 0) return null;

        var notes = new List<NoteIn>();
        foreach (var n in untitled)
        {
            var id = AutoJson.Text(n, "id")!;
            if (await context.ReadAsync("notes_read", new { id }, ct) is not { } note || AutoJson.Text(note, "text") is not { Length: > 0 } text) continue;
            notes.Add(new NoteIn(id, text.Length <= ShownLength ? text : text[..ShownLength] + "…"));
        }
        if (notes.Count == 0) return null;

        var answer = await context.AskAsync($$"""
            Give each note below a short title (at most 8 words, in the note's own language) and one to three tags (one or two
            words each, lower case, no #). Answer with JSON only, no other text: [{"id":"…","title":"…","tags":["…"]}].
            {{JsonSerializer.Serialize(notes, Compact)}}
            """, ct).ConfigureAwait(false);
        // Answered: these notes are not asked about again, whatever the answer gave them.
        context.Remember(untitled.Select(n => AutoJson.Text(n, "id")!));

        var done = new List<AiJobAction>();
        var lines = new List<string>();
        foreach (var (id, title, tags) in Parse(answer))
        {
            if (notes.All(n => n.Id != id)) continue;
            if (await context.ChangeAsync("notes_retitle", new { id, title, tags }, $"Đặt tiêu đề “{title}”", ct) is not { } action) continue;
            done.Add(action);
            var tagLine = JsonDocument.Parse(action.ResultJson).RootElement.TryGetProperty("tags", out var t) ? t.GetString() : null;
            lines.Add(string.IsNullOrEmpty(tagLine) ? $"“{title}”" : $"“{title}” {tagLine}");
        }
        if (done.Count == 0) return null;
        return new AutoBrief($"Em đặt tiêu đề cho {done.Count} ghi chú",
            string.Join("\n", lines) + "\nAnh muốn bỏ thì bảo em “hoàn tác tiêu đề ghi chú” nha.", done);
    }

    /// <summary>The agent's answer: its JSON array, even with words around it.</summary>
    internal static IEnumerable<(string Id, string Title, IReadOnlyList<string> Tags)> Parse(string answer)
    {
        var start = answer.IndexOf('[');
        var end = answer.LastIndexOf(']');
        if (start < 0 || end <= start) yield break;
        JsonElement items;
        try
        {
            items = JsonDocument.Parse(answer[start..(end + 1)]).RootElement.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }
        foreach (var item in AutoJson.Items(items))
        {
            if (AutoJson.Text(item, "id") is not { Length: > 0 } id || AutoJson.Text(item, "title")?.Trim() is not { Length: > 0 } title) continue;
            IReadOnlyList<string> tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? [.. t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(3)]
                : [];
            yield return (id, title, tags);
        }
    }

    private sealed record NoteIn(string Id, string Text);

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
