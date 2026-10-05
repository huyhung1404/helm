using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Helm.Modules.Missions;

/// <summary>
/// The resources of a step: the limits they keep to, the one line older Helm versions read, the import form and the
/// text a copy puts on the clipboard. Nothing here knows a subject: labels and columns are the plan's own words.
/// </summary>
public static class MissionResources
{
    private static readonly Regex Address = new(@"https?://[^\s<>""')\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A step's resources: its details when it has them, else its lines (all older Helm versions write).</summary>
    public static IReadOnlyList<MissionResource> Of(MissionStep step) =>
        step.Materials.Count > 0 ? step.Materials : step.Resources.Select(FromLine).ToList();

    /// <summary>A text that changes when any of the resources does (to rebuild the rows only then).</summary>
    public static string Signature(IReadOnlyList<MissionResource> resources) =>
        resources.Count == 0 ? "" : JsonSerializer.Serialize(resources);

    /// <summary>"Title — https://…", a bare address or a book title, as a resource.</summary>
    public static MissionResource FromLine(string line)
    {
        var text = line.Trim();
        if (Address.Match(text) is not { Success: true } m) return new MissionResource { Title = text };
        var url = m.Value.TrimEnd('.', ',', ';');
        // Only an address at the end is cut off its title: "Course — https://…".
        var title = text.EndsWith(url, StringComparison.Ordinal) ? text[..^url.Length].TrimEnd(' ', '—', '–', '-', ':', '|', ',') : text;
        return new MissionResource { Title = title, Url = url };
    }

    /// <summary>The one line older Helm versions show (and the import's plain form): "Title — https://…".</summary>
    public static string Line(MissionResource r)
    {
        var name = r.Title.Length > 0 ? r.Title
            : r.Url.Length > 0 ? ""
            : r.Text.Length > 0 ? FirstLine(r.Text, 120)
            : r.Rows.Count > 0 ? $"{(r.Label.Length > 0 ? r.Label + ": " : "")}{string.Join(", ", r.Rows.Take(8).Select(row => row.FirstOrDefault() ?? ""))}"
            : r.Label;
        var line = name.Length > 0 && r.Url.Length > 0 ? $"{name} — {r.Url}" : name.Length > 0 ? name : r.Url;
        return MissionLimits.Clip(line, MissionLimits.ResourceText);
    }

    /// <summary>True when the resource holds more than its line: a label, a text or a table.</summary>
    public static bool HasDetails(MissionResource r) => r.Label.Length > 0 || r.Text.Length > 0 || r.Rows.Count > 0;

    /// <summary>An absolute http or https address (the only kind a resource opens).</summary>
    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The resources within the limits: fields clipped, an address that is not http(s) left out, every row as wide as
    /// the columns, empty rows and resources dropped, at most <see cref="MissionLimits.Resources"/>, and the rows and
    /// texts of the whole step kept within their budgets.
    /// </summary>
    public static List<MissionResource> Clean(IEnumerable<MissionResource>? resources)
    {
        var list = new List<MissionResource>();
        var rowsLeft = MissionLimits.StepResourceRows;
        var textLeft = MissionLimits.StepResourceBody;
        foreach (var r in resources ?? [])
        {
            if (list.Count == MissionLimits.Resources) break;
            var url = MissionLimits.Clip(r.Url, MissionLimits.ResourceUrl);
            var text = MissionLimits.Clip(r.Text, Math.Min(MissionLimits.ResourceBody, textLeft));
            var columns = (r.Columns ?? []).Take(MissionLimits.ResourceColumns).Select(c => MissionLimits.Clip(c, MissionLimits.ColumnName)).ToList();
            var width = columns.Count > 0 ? columns.Count : Math.Min(MissionLimits.ResourceColumns, Math.Max(1, (r.Rows ?? []).Select(row => row?.Count ?? 0).DefaultIfEmpty(1).Max()));
            var rows = (r.Rows ?? [])
                .Select(row => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => MissionLimits.Clip(row is not null && i < row.Count ? row[i] : "", MissionLimits.Cell)).ToList())
                .Where(row => row.Any(c => c.Length > 0))
                .Take(Math.Min(MissionLimits.ResourceRows, rowsLeft))
                .ToList();
            var clean = new MissionResource
            {
                Label = MissionLimits.Clip(r.Label, MissionLimits.ResourceLabel),
                Title = MissionLimits.Clip(r.Title, MissionLimits.ResourceTitle),
                Url = IsWebAddress(url) ? url : "",
                Text = text,
                Columns = rows.Count > 0 && columns.Any(c => c.Length > 0) ? columns : [],
                Rows = rows,
            };
            if (clean.Title.Length == 0 && clean.Url.Length == 0 && clean.Text.Length == 0 && clean.Rows.Count == 0) continue;
            rowsLeft -= rows.Count;
            textLeft -= text.Length;
            list.Add(clean);
        }
        return list;
    }

    /// <summary>"45 rows", "1 row", or empty.</summary>
    public static string Count(MissionResource r) => r.Rows.Count switch
    {
        0 => "",
        1 => "1 row",
        var n => $"{n} rows",
    };

    /// <summary>
    /// The resource as plain text, for the clipboard. The table goes as tab-separated lines (with its columns first), so
    /// it pastes as a table into a spreadsheet or a flashcard app.
    /// </summary>
    public static string ToText(MissionResource r)
    {
        // "\n" on every platform, so a copy pastes the same from the PC and the phone.
        var b = new StringBuilder();
        void Line(string text) => b.Append(text).Append('\n');
        var head = r.Title.Length > 0 ? r.Title : r.Label;
        if (head.Length > 0) Line(r.Label.Length > 0 && head != r.Label ? $"{head} ({r.Label})" : head);
        if (r.Url.Length > 0) Line(r.Url);
        if (r.Text.Length > 0)
        {
            if (b.Length > 0) Line("");
            Line(r.Text);
        }
        if (r.Rows.Count > 0)
        {
            if (b.Length > 0) Line("");
            if (r.Columns.Count > 0) Line(string.Join('\t', r.Columns.Select(Cell)));
            foreach (var row in r.Rows) Line(string.Join('\t', row.Select(Cell)));
        }
        // A last empty cell keeps its tab: it still lines the table up.
        return b.ToString().TrimEnd('\n') + "\n";

        static string Cell(string c) => c.Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');
    }

    /// <summary>The import form: a plain line for a link or a book title, else an object with its non-empty fields.</summary>
    public static JsonNode ToJson(MissionResource r)
    {
        if (!HasDetails(r)) return JsonValue.Create(Line(r));
        var node = new JsonObject();
        if (r.Label.Length > 0) node["label"] = r.Label;
        if (r.Title.Length > 0) node["title"] = r.Title;
        if (r.Url.Length > 0) node["url"] = r.Url;
        if (r.Text.Length > 0) node["text"] = r.Text;
        if (r.Columns.Count > 0) node["columns"] = new JsonArray(r.Columns.Select(c => (JsonNode?)c).ToArray());
        if (r.Rows.Count > 0) node["rows"] = new JsonArray(r.Rows.Select(row => (JsonNode?)new JsonArray(row.Select(c => (JsonNode?)c).ToArray())).ToArray());
        return node;
    }

    private static string FirstLine(string text, int max)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }
}
