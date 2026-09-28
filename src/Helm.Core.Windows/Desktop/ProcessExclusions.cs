namespace Helm.Core.Desktop;

/// <summary>Case-insensitive process-name exclusion list ("Unity.exe", "Unity" and "unity.EXE" all match).</summary>
public sealed class ProcessExclusions
{
    private readonly HashSet<string> _names;

    public ProcessExclusions(IEnumerable<string>? names)
    {
        _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names ?? [])
        {
            var name = Normalize(raw);
            if (name.Length > 0) _names.Add(name);
        }
    }

    public int Count => _names.Count;

    public bool IsExcluded(string? processName) =>
        !string.IsNullOrEmpty(processName) && _names.Contains(Normalize(processName));

    /// <summary>Splits a textarea (one name per line, commas allowed) into a clean list.</summary>
    public static List<string> ParseLines(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Normalize(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }
}
