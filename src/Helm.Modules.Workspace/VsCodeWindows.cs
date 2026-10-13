namespace Helm.Modules.Workspace;

/// <summary>
/// Finds the top-level window of a VS Code workspace by its title. VS Code's default title is
/// <c>[●] [file - ]workspace[ - profile] - Visual Studio Code</c>; every window of one VS Code belongs to the same
/// process, so the title is what tells them apart.
/// </summary>
public static class VsCodeWindows
{
    private static readonly string[] AppNames = ["Visual Studio Code", "Visual Studio Code - Insiders"];

    /// <summary>True when <paramref name="title"/> is a VS Code window showing workspace <paramref name="name"/>.</summary>
    public static bool Matches(string title, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var app in AppNames)
        {
            var suffix = " - " + app;
            if (!title.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var parts = title[..^suffix.Length].TrimStart('●', ' ').Split(" - ");
            return parts.Any(p => string.Equals(p.Trim(), name, StringComparison.Ordinal)) ||
                   // Multi-root workspaces show "name (Workspace)".
                   parts.Any(p => string.Equals(p.Trim(), name + " (Workspace)", StringComparison.Ordinal));
        }
        return false;
    }

    /// <summary>The one window of <paramref name="name"/>; null when there is none or more than one (ambiguous).</summary>
    public static nint Find(IEnumerable<(nint Handle, string Title)> windows, string name)
    {
        var found = windows.Where(w => Matches(w.Title, name)).Select(w => w.Handle).Distinct().Take(2).ToList();
        return found.Count == 1 ? found[0] : 0;
    }
}
