using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Helm.Modules.Missions;

/// <summary>What the import made of a text: a draft to preview, or the errors that stop it, plus warnings.</summary>
public sealed record MissionImportResult(MissionDraft? Draft, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Ok => Draft is not null && Errors.Count == 0;
}

/// <summary>
/// Reads a mission from the JSON an AI wrote (see <see cref="MissionPrompt"/>), forgiving what AIs usually get wrong:
/// text or a ```json fence around it, typographic quotes as JSON quotes, trailing commas, comments, steps without phases,
/// numbers written as text, missing optional fields. Claude's <c>mission_create</c> goes through the same rules.
/// </summary>
public static class MissionImport
{
    /// <summary>The format version this Helm writes and reads (<c>"helmMission": 1</c>).</summary>
    public const int Version = 1;

    private const int MaxErrors = 8;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    public static MissionImportResult Parse(string? text, bool requireTitle = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail("Paste the JSON the AI gave you.");
        var json = Extract(text, out var cutOff);
        if (json is null)
            return Fail(cutOff
                ? "The JSON is cut off before its end. Ask the AI to send the whole JSON again in one code block."
                : "There is no JSON here. Paste the AI's answer: it starts with { and ends with }.");

        JsonDocument? doc = null;
        string? error = null;
        // As written first (typographic quotes inside texts are fine), then with them turned into plain quotes.
        foreach (var candidate in new[] { json, PlainQuotes(json) }.Distinct())
        {
            try
            {
                doc = JsonDocument.Parse(candidate, Options);
                break;
            }
            catch (JsonException ex)
            {
                error ??= ex.LineNumber is { } line ? $"The JSON has a mistake on line {line + 1}: {Short(ex.Message)}" : $"The JSON has a mistake: {Short(ex.Message)}";
            }
        }
        if (doc is null) return Fail(error ?? "The JSON could not be read.");
        using (doc) return Read(doc.RootElement, requireTitle);
    }

    /// <summary>Reads an already parsed object (Claude's tool arguments).</summary>
    /// <param name="requireTitle">False for a new plan of an existing mission, which keeps its own title.</param>
    public static MissionImportResult Read(JsonElement root, bool requireTitle = true)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) return Fail("The JSON must be one object: { \"title\": …, \"phases\": […] }.");

        if (Number(root, "helmMission", "helm_mission", "version") is { } version && version > Version)
            return Fail($"This mission uses format {version}, newer than this Helm reads ({Version}). Update Helm and try again.");

        var title = Text(root, "title", "name", "mission");
        if (title.Length == 0 && requireTitle) errors.Add("The mission has no \"title\".");
        else if (title.Length > MissionLimits.Title) warnings.Add($"The title is long; it was cut to {MissionLimits.Title} characters.");

        DateOnly? deadline = null;
        var deadlineText = Text(root, "deadline", "due", "targetDate", "target_date");
        if (deadlineText.Length > 0)
        {
            if (TryDay(deadlineText, out var day)) deadline = day;
            else warnings.Add($"The deadline \"{deadlineText}\" is not a date like 2027-03-01; it was left out.");
        }

        var phases = new List<PhaseDraft>();
        var totalSteps = 0;
        if (Array(root, "phases", "stages", "milestones") is { } phaseArray)
        {
            var number = 0;
            foreach (var p in phaseArray.EnumerateArray())
            {
                number++;
                if (p.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"Phase {number} is not an object.");
                    continue;
                }
                var steps = Steps(p, $"Phase {number}", errors, warnings);
                if (steps.Count == 0)
                {
                    warnings.Add($"Phase {number} has no steps; it was left out.");
                    continue;
                }
                totalSteps += steps.Count;
                phases.Add(new PhaseDraft(Text(p, "title", "name", "phase"), Text(p, "reward"), steps));
            }
        }
        else if (Array(root, "steps", "tasks") is not null)
        {
            // No phases: one phase named after the mission.
            var steps = Steps(root, "The mission", errors, warnings);
            totalSteps += steps.Count;
            if (steps.Count > 0) phases.Add(new PhaseDraft(title, "", steps));
        }
        else
        {
            errors.Add("The mission has no \"phases\" (or \"steps\").");
        }

        if (errors.Count == 0 && totalSteps == 0) errors.Add("The mission has no steps.");
        if (phases.Count > MissionLimits.Phases) errors.Add($"The mission has {phases.Count} phases; at most {MissionLimits.Phases}. Ask the AI for fewer, bigger phases.");
        if (totalSteps > MissionLimits.Steps) errors.Add($"The mission has {totalSteps} steps; at most {MissionLimits.Steps}. Ask the AI for fewer, bigger steps.");

        if (errors.Count > 0) return new MissionImportResult(null, Cap(errors), warnings);
        var draft = new MissionDraft(
            MissionLimits.Clip(title, MissionLimits.Title),
            MissionLimits.Clip(Text(root, "goal", "objective", "description"), MissionLimits.Goal),
            MissionLimits.Clip(Text(root, "note", "notes", "warning"), MissionLimits.Note),
            MissionLimits.Clip(Text(root, "reward"), MissionLimits.Reward),
            deadline,
            phases);
        return new MissionImportResult(draft, [], warnings);
    }

    private static List<StepDraft> Steps(JsonElement parent, string where, List<string> errors, List<string> warnings)
    {
        var list = new List<StepDraft>();
        if (Array(parent, "steps", "tasks") is not { } array) return list;
        var number = 0;
        foreach (var s in array.EnumerateArray())
        {
            number++;
            var at = $"{where}, step {number}";
            if (s.ValueKind == JsonValueKind.String)
            {
                // A bare title is a step too.
                if (s.GetString() is { Length: > 0 } bare && bare.Trim().Length > 0) list.Add(new StepDraft(bare.Trim()));
                else errors.Add($"{at} has no title.");
                continue;
            }
            if (s.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{at} is not an object.");
                continue;
            }
            var title = Text(s, "title", "name", "task", "step");
            if (title.Length == 0)
            {
                errors.Add($"{at} has no title.");
                continue;
            }
            var estimate = Number(s, "estimateDays", "estimate_days", "days", "durationDays", "duration_days", "estimate");
            var days = estimate ?? 1;
            if (estimate is { } e && (e < MissionLimits.MinEstimateDays || e > MissionLimits.MaxEstimateDays))
                warnings.Add($"{at}: {e.ToString(CultureInfo.InvariantCulture)} days was changed to {MissionsStore.ClampEstimate(e).ToString(CultureInfo.InvariantCulture)} (between {MissionLimits.MinEstimateDays.ToString(CultureInfo.InvariantCulture)} and {MissionLimits.MaxEstimateDays}).");
            var checklist = Strings(s, ["checklist", "subtasks", "items", "todo"], "text", "title", "name");
            if (checklist.Count > MissionLimits.ChecklistItems) warnings.Add($"{at}: only the first {MissionLimits.ChecklistItems} checklist items were kept.");
            list.Add(new StepDraft(
                title,
                Text(s, "description", "details", "how", "instructions"),
                Text(s, "doneWhen", "done_when", "doneCriteria", "done_criteria", "criteria", "definitionOfDone"),
                MissionsStore.ClampEstimate(days),
                checklist.Take(MissionLimits.ChecklistItems).ToList(),
                Resources(s, at, warnings)));
        }
        return list;
    }

    /// <summary>
    /// A step's resources: a string is a link or a book title; an object may also carry a label, a text (the theory,
    /// where the test is) and a table with columns the plan names itself. Kept within <see cref="MissionLimits"/>.
    /// </summary>
    private static List<MissionResource> Resources(JsonElement step, string at, List<string> warnings)
    {
        var raw = new List<MissionResource>();
        if (Array(step, "resources", "links", "materials", "references") is not { } array) return raw;
        foreach (var r in array.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.String && r.GetString() is { } s && s.Trim().Length > 0)
                raw.Add(MissionResources.FromLine(s));
            else if (r.ValueKind == JsonValueKind.Object)
            {
                var url = Text(r, "url", "link", "href");
                if (url.Length > 0 && !MissionResources.IsWebAddress(url)) warnings.Add($"{at}: \"{Short(url, 60)}\" is not a web address; the link was left out.");
                var (columns, rows) = Table(r);
                var resource = new MissionResource
                {
                    Label = Text(r, "label", "kind", "type", "category", "tag"),
                    Title = Text(r, "title", "name"),
                    Url = url,
                    Text = Text(r, "text", "content", "body", "details", "description", "explanation", "notes"),
                    Columns = columns,
                    Rows = rows,
                };
                // The old form { "text": "A book" } (or with a url): a title, not a text.
                if (resource is { Title.Length: 0, Label.Length: 0, Rows.Count: 0 } && resource.Text.Length <= MissionLimits.ResourceTitle && !resource.Text.Contains('\n'))
                    resource = resource with { Title = resource.Text, Text = "" };
                raw.Add(resource);
            }
        }
        var clean = MissionResources.Clean(raw);
        if (raw.Count > MissionLimits.Resources) warnings.Add($"{at}: only the first {MissionLimits.Resources} resources were kept.");
        if (raw.Take(MissionLimits.Resources).Sum(r => r.Rows.Count(row => row.Any(c => c.Length > 0))) > clean.Sum(r => r.Rows.Count))
            warnings.Add($"{at}: some table rows were left out (at most {MissionLimits.ResourceRows} in a resource and {MissionLimits.StepResourceRows} in a step).");
        if (raw.Any(r => r.Columns.Count > MissionLimits.ResourceColumns || r.Rows.Any(row => row.Count > MissionLimits.ResourceColumns)))
            warnings.Add($"{at}: a table has more than {MissionLimits.ResourceColumns} columns; the others were left out.");
        return clean;
    }

    /// <summary>
    /// A resource's table in any of the shapes AIs write: <c>columns</c> + <c>rows</c> (rows as arrays or as objects),
    /// or a list (<c>items</c>, <c>rows</c>, <c>entries</c>…) of objects (their keys become the columns, in the order first
    /// seen), of arrays, or of plain strings (one column).
    /// </summary>
    private static (List<string> Columns, List<IReadOnlyList<string>> Rows) Table(JsonElement resource)
    {
        var columns = Array(resource, "columns", "headers", "header") is { } head
            ? head.EnumerateArray().Select(Cell).ToList()
            : [];
        var rows = new List<IReadOnlyList<string>>();
        if (Array(resource, "rows", "items", "entries", "table", "list", "words", "terms") is not { } array) return (columns, rows);
        // Objects first: their keys add to the columns.
        foreach (var item in array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            foreach (var p in item.EnumerateObject())
                if (!columns.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) columns.Add(p.Name);
        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Object:
                    rows.Add(columns.Select(c => item.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, c, StringComparison.OrdinalIgnoreCase)) is { Value: var v } ? Cell(v) : "").ToList());
                    break;
                case JsonValueKind.Array:
                    rows.Add(item.EnumerateArray().Select(Cell).ToList());
                    break;
                case JsonValueKind.String or JsonValueKind.Number:
                    rows.Add([Cell(item)]);
                    break;
            }
        }
        return (columns, rows);
    }

    /// <summary>A table cell: text, a number, or a list of texts on one line.</summary>
    private static string Cell(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!.Trim(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Cell).Where(c => c.Length > 0)),
        _ => "",
    };

    // ---- Finding the JSON ----------------------------------------------------------------------------------------

    /// <summary>
    /// The JSON object in a pasted answer: the first <c>{</c> to its matching <c>}</c>, skipping anything around it
    /// (explanations, a ```json fence). <paramref name="cutOff"/> tells a JSON that never closes from no JSON at all.
    /// </summary>
    internal static string? Extract(string text, out bool cutOff)
    {
        cutOff = false;
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var quote = '"';
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote || (quote == '“' && c == '”')) inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                case '“':
                    inString = true;
                    quote = c;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }
        cutOff = true;
        return null;
    }

    /// <summary>Typographic double quotes used as JSON quotes (some chat apps "smarten" them) → plain quotes.</summary>
    internal static string PlainQuotes(string json) =>
        new StringBuilder(json).Replace('“', '"').Replace('”', '"').Replace('„', '"').Replace('«', '"').Replace('»', '"').ToString();

    // ---- Reading values ------------------------------------------------------------------------------------------

    /// <summary>A property by any of its names, ignoring case.</summary>
    private static JsonElement? Prop(JsonElement obj, params string[] names)
    {
        foreach (var p in obj.EnumerateObject())
            foreach (var name in names)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
                    return p.Value;
        return null;
    }

    private static string Text(JsonElement obj, params string[] names) => Prop(obj, names) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        // Some AIs write a list of lines where a text was asked for.
        { ValueKind: JsonValueKind.Array } v => string.Join("\n", v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim())),
        _ => "",
    };

    private static double? Number(JsonElement obj, params string[] names) => Prop(obj, names) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(LeadingNumber(v.GetString()!), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    /// <summary>"3", "3 days", "2.5d", "1,5" → the number at the start.</summary>
    private static string LeadingNumber(string s)
    {
        s = s.Trim().Replace(',', '.');
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        return s[..end];
    }

    private static JsonElement? Array(JsonElement obj, params string[] names) =>
        Prop(obj, names) is { ValueKind: JsonValueKind.Array } v ? v : null;

    /// <summary>A list of strings, where an item may also be an object with a text field.</summary>
    private static List<string> Strings(JsonElement obj, string[] names, params string[] textFields)
    {
        var list = new List<string>();
        if (Array(obj, names) is not { } array) return list;
        foreach (var item in array.EnumerateArray())
        {
            var text = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString()!.Trim(),
                JsonValueKind.Object => Text(item, textFields),
                _ => "",
            };
            if (text.Length > 0) list.Add(text);
        }
        return list;
    }

    /// <summary>"2027-03-01" (also "2027/03/01", or a full date-time such as "2027-03-01T00:00:00Z").</summary>
    internal static bool TryDay(string text, out DateOnly day)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return true;
        if (text.Length > 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return true;
        day = default;
        return false;
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Short(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).TrimEnd('.') + ".";
    }

    private static List<string> Cap(List<string> errors) =>
        errors.Count <= MaxErrors ? errors : [.. errors.Take(MaxErrors), $"…and {errors.Count - MaxErrors} more."];

    private static MissionImportResult Fail(string error) => new(null, [error], []);
}
