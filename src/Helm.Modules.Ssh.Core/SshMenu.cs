using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helm.Modules.Ssh;

public enum MenuItemKind
{
    /// <summary>A button: runs when asked, with its parameters.</summary>
    Action,

    /// <summary>Shown as soon as it is opened, and refreshed every <see cref="MenuItem.Refresh"/> seconds.</summary>
    View,
}

public enum MenuOutput
{
    /// <summary>Text shown as it comes (a deploy's log); the exit code says whether it worked.</summary>
    Stream,

    Text,

    /// <summary>JSON: columns and rows; rows may carry actions.</summary>
    Table,

    /// <summary>JSON: a few labelled values with a level (ok, warn, bad).</summary>
    Stats,
}

public enum MenuDanger
{
    None,

    /// <summary>Asked before it runs.</summary>
    Confirm,

    /// <summary>Asked with the item's own warning and the exact command shown.</summary>
    High,
}

public enum MenuParamType
{
    Choice,
    Text,
    Number,
    Bool,
}

public sealed record MenuParam(
    string Id, string Title, MenuParamType Type, IReadOnlyList<string> Choices, bool DynamicChoices, string? Pattern, int MaxLength,
    double? Min, double? Max, string? Default, bool Required);

public sealed record MenuItem(
    string Id, string Title, string? Description, MenuItemKind Kind, MenuOutput Output, MenuDanger Danger, string? Confirm,
    IReadOnlyList<MenuParam> Params, int Refresh, IReadOnlyList<string> RowActions);

public sealed record MenuGroup(string Title, IReadOnlyList<MenuItem> Items);

/// <summary>What a server's menu offers (<c>menu describe</c>).</summary>
public sealed record MenuDescription(string Title, IReadOnlyList<MenuGroup> Groups)
{
    public MenuItem? Find(string id) => Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == id);
}

public sealed record MenuColumn(string Id, string Title);

/// <param name="Key">Identifies the row for its actions (e.g. the process name).</param>
public sealed record MenuRow(string? Key, IReadOnlyDictionary<string, string> Cells);

public sealed record MenuTable(IReadOnlyList<MenuColumn> Columns, IReadOnlyList<MenuRow> Rows);

/// <param name="Level">ok, warn or bad (anything else reads as plain).</param>
public sealed record MenuStat(string Label, string Value, string Level);

/// <summary>The server's menu said something Helm cannot use.</summary>
public sealed class MenuFormatException(string message) : Exception(message);

/// <summary>
/// The contract between Helm and a server's menu (docs/ssh-menu.md): the server keeps one executable (by default
/// <see cref="DefaultPath"/>) that describes its menu as JSON and runs its items. Helm only ever runs that file with
/// arguments it built and quoted itself, after checking every value against what the menu declared. Everything a
/// server sends is bounded, so a broken or hostile menu cannot flood Helm.
/// </summary>
public static class SshMenu
{
    public const int Protocol = 1;
    public const string DefaultPath = "~/.helm/menu";

    private const int MaxGroups = 32;
    private const int MaxItems = 200;
    private const int MaxParams = 16;
    private const int MaxChoices = 500;
    private const int MaxColumns = 20;
    private const int MaxRows = 2000;
    private const int MaxStats = 50;
    private const int MaxText = 500;
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex PathPattern = new("^(~/)?[A-Za-z0-9._/-]{1,255}$", RegexOptions.CultureInvariant);

    /// <summary>A menu path Helm can put on a command line as it is: ~/ and plain path characters only.</summary>
    public static bool IsValidPath(string? path) => path is not null && PathPattern.IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// The command line for the menu: its path, then each argument in single quotes (POSIX), so nothing in a value can
    /// end the quoting or run anything.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not a plain path.</exception>
    public static string Command(string menuPath, params string[] args)
    {
        if (!IsValidPath(menuPath)) throw new ArgumentException("The menu path may only hold letters, digits and . _ - /, optionally after ~/.", nameof(menuPath));
        var line = new StringBuilder(menuPath);
        foreach (var arg in args) line.Append(' ').Append(Quote(arg));
        return line.ToString();
    }

    /// <summary>POSIX single quoting: 'it'\''s'.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The arguments for running <paramref name="item"/> with these values (already checked).</summary>
    public static string[] RunArguments(MenuItem item, IReadOnlyDictionary<string, string> values) =>
        ["run", item.Id, .. item.Params.Where(p => values.ContainsKey(p.Id)).Select(p => $"--{p.Id}={values[p.Id]}")];

    /// <summary>Why <paramref name="value"/> is not acceptable for <paramref name="param"/>; null when it is.</summary>
    /// <param name="choices">The choices in force (the server's list for dynamic choices).</param>
    public static string? Validate(MenuParam param, string? value, IReadOnlyList<string> choices)
    {
        value ??= "";
        if (value.Length == 0) return param.Required ? $"{param.Title} is needed." : null;
        if (value.Any(char.IsControl)) return $"{param.Title} cannot hold control characters.";
        switch (param.Type)
        {
            case MenuParamType.Choice:
                return choices.Contains(value, StringComparer.Ordinal) ? null : $"Choose {param.Title} from the list.";
            case MenuParamType.Bool:
                return value is "true" or "false" ? null : $"{param.Title} is yes or no.";
            case MenuParamType.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    return $"{param.Title} must be a number.";
                if (param.Min is { } min && number < min) return $"{param.Title} is at least {min.ToString(CultureInfo.InvariantCulture)}.";
                if (param.Max is { } max && number > max) return $"{param.Title} is at most {max.ToString(CultureInfo.InvariantCulture)}.";
                return null;
            default:
                if (value.Length > param.MaxLength) return $"{param.Title} is at most {param.MaxLength} characters.";
                if (param.Pattern is { } pattern)
                {
                    try
                    {
                        if (!Regex.IsMatch(value, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
                            return $"{param.Title} is not in the expected form.";
                    }
                    catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                    {
                        return $"{param.Title} cannot be checked (the menu's pattern is broken).";
                    }
                }
                return null;
        }
    }

    // ---- Parsing ---------------------------------------------------------------------------------------------------

    public static MenuDescription ParseDescription(string json)
    {
        using var doc = Parse(json);
        var root = Object(doc.RootElement, "menu");
        var protocol = root.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        if (protocol != Protocol) throw new MenuFormatException($"The menu speaks protocol {protocol}; this Helm speaks {Protocol}.");
        var groups = new List<MenuGroup>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in Array(root, "groups", MaxGroups))
        {
            var items = new List<MenuItem>();
            foreach (var i in Array(Object(g, "group"), "items", MaxItems))
            {
                var item = ParseItem(Object(i, "item"));
                if (!ids.Add(item.Id)) throw new MenuFormatException($"The menu lists \"{item.Id}\" twice.");
                items.Add(item);
            }
            if (ids.Count > MaxItems) throw new MenuFormatException("The menu has too many items.");
            groups.Add(new MenuGroup(Text(g, "title") ?? "", items));
        }
        var description = new MenuDescription(Text(root, "title") ?? "Menu", groups);
        foreach (var item in groups.SelectMany(x => x.Items))
        {
            foreach (var action in item.RowActions)
                if (description.Find(action) is null) throw new MenuFormatException($"\"{item.Id}\" refers to \"{action}\", which the menu does not have.");
        }
        return description;
    }

    public static IReadOnlyList<string> ParseChoices(string json)
    {
        using var doc = Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new MenuFormatException("Choices must be a JSON list.");
        return Strings(doc.RootElement, MaxChoices);
    }

    public static MenuTable ParseTable(string json)
    {
        using var doc = Parse(json);
        var root = Object(doc.RootElement, "table");
        var columns = Array(root, "columns", MaxColumns).Select(c =>
        {
            var o = Object(c, "column");
            return new MenuColumn(Id(o, "column"), Text(o, "title") ?? Id(o, "column"));
        }).ToList();
        var rows = Array(root, "rows", MaxRows).Select(r =>
        {
            var o = Object(r, "row");
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            if (o.TryGetProperty("cells", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                foreach (var column in columns)
                    if (c.TryGetProperty(column.Id, out var v)) cells[column.Id] = Clip(Scalar(v));
            }
            return new MenuRow(Text(o, "key"), cells);
        }).ToList();
        return new MenuTable(columns, rows);
    }

    public static IReadOnlyList<MenuStat> ParseStats(string json)
    {
        using var doc = Parse(json);
        var root = Object(doc.RootElement, "stats");
        return Array(root, "stats", MaxStats).Select(s =>
        {
            var o = Object(s, "stat");
            return new MenuStat(Text(o, "label") ?? "", o.TryGetProperty("value", out var v) ? Clip(Scalar(v)) : "", Text(o, "level") ?? "");
        }).ToList();
    }

    /// <summary>
    /// The values a row action runs with: each parameter takes the row's cell of the same id, and the row's key fills
    /// the parameters no cell names (a process row restarts its own process).
    /// </summary>
    public static Dictionary<string, string> RowValues(MenuItem action, MenuRow row)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var param in action.Params)
        {
            if (row.Cells.TryGetValue(param.Id, out var cell)) values[param.Id] = cell;
            else if (row.Key is { } key) values[param.Id] = key;
        }
        return values;
    }

    private static MenuItem ParseItem(JsonElement o)
    {
        var id = Id(o, "item");
        var kind = Text(o, "kind") == "view" ? MenuItemKind.View : MenuItemKind.Action;
        var output = Text(o, "output") switch
        {
            "stream" => MenuOutput.Stream,
            "table" => MenuOutput.Table,
            "stats" => MenuOutput.Stats,
            _ => MenuOutput.Text,
        };
        var danger = Text(o, "danger") switch
        {
            "high" => MenuDanger.High,
            "confirm" => MenuDanger.Confirm,
            _ => MenuDanger.None,
        };
        var parameters = new List<MenuParam>();
        var paramIds = new HashSet<string>(StringComparer.Ordinal);
        if (o.TryGetProperty("params", out _))
        {
            foreach (var p in Array(o, "params", MaxParams))
            {
                var param = ParseParam(Object(p, "parameter"));
                if (!paramIds.Add(param.Id)) throw new MenuFormatException($"\"{id}\" has the parameter \"{param.Id}\" twice.");
                parameters.Add(param);
            }
        }
        var refresh = o.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.Number ? Math.Clamp(r.GetInt32(), 0, 3600) : 0;
        if (refresh is > 0 and < 2) refresh = 2;
        var rowActions = o.TryGetProperty("rowActions", out var ra) && ra.ValueKind == JsonValueKind.Array ? Strings(ra, 8) : [];
        return new MenuItem(id, Text(o, "title") ?? id, Text(o, "description"), kind, output, danger, Text(o, "confirm"), parameters, refresh, rowActions);
    }

    private static MenuParam ParseParam(JsonElement o)
    {
        var id = Id(o, "parameter");
        var type = Text(o, "type") switch
        {
            "choice" => MenuParamType.Choice,
            "number" => MenuParamType.Number,
            "bool" => MenuParamType.Bool,
            _ => MenuParamType.Text,
        };
        var dynamic = o.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == "dynamic";
        IReadOnlyList<string> choices = c.ValueKind == JsonValueKind.Array ? Strings(c, MaxChoices) : [];
        if (type == MenuParamType.Choice && !dynamic && choices.Count == 0) throw new MenuFormatException($"The choice \"{id}\" offers nothing to choose.");
        double? Number(string name) => o.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : null;
        var maxLength = (int)Math.Clamp(Number("maxLength") ?? 200, 1, 4000);
        var required = !o.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.False;
        return new MenuParam(id, Text(o, "title") ?? id, type, choices, dynamic, Text(o, "pattern"), maxLength, Number("min"), Number("max"), Text(o, "default"), required);
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new MenuFormatException("The menu did not answer with valid JSON: " + ex.Message);
        }
    }

    private static JsonElement Object(JsonElement e, string what) =>
        e.ValueKind == JsonValueKind.Object ? e : throw new MenuFormatException($"A {what} must be a JSON object.");

    private static IEnumerable<JsonElement> Array(JsonElement o, string name, int max)
    {
        if (!o.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) throw new MenuFormatException($"\"{name}\" must be a JSON list.");
        if (a.GetArrayLength() > max) throw new MenuFormatException($"\"{name}\" has more than {max} entries.");
        return a.EnumerateArray().ToList();
    }

    private static List<string> Strings(JsonElement a, int max)
    {
        if (a.GetArrayLength() > max) throw new MenuFormatException($"The list has more than {max} entries.");
        return a.EnumerateArray().Select(v => Clip(Scalar(v))).ToList();
    }

    private static string Id(JsonElement o, string what)
    {
        var id = Text(o, "id");
        return id is not null && IdPattern.IsMatch(id) ? id : throw new MenuFormatException($"A {what} needs an id of lowercase letters, digits, - and _.");
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString() ?? "") : null;

    private static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        _ => "",
    };

    private static string Clip(string s) => s.Length <= MaxText ? s : s[..MaxText];
}

/// <summary>How a menu command ended: its exit code (null when it was stopped or the connection broke) and its error output.</summary>
public sealed record MenuRunResult(int? ExitCode, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs a menu command on the server (an open <see cref="SshSession"/>; tests use a fake).</summary>
public interface IMenuRunner
{
    Task<MenuRunResult> RunStreamingAsync(string command, Action<string>? output, CancellationToken ct);
}
