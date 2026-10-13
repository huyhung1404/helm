using System.Text.RegularExpressions;

namespace Helm.Modules.Workspace;

/// <summary>
/// Claude Code's own record of past sessions: <c>~/.claude/projects/&lt;folder with every non-alphanumeric character
/// as '-'&gt;/&lt;session id&gt;.jsonl</c>. Helm reads it when a VS Code window connects, so sessions that were open
/// before Helm started are listed too (hooks only tell about what happens while Helm runs).
/// </summary>
public static partial class ClaudeProjects
{
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>The project folder name Claude uses for <paramref name="folder"/>.</summary>
    public static string ProjectKey(string folder) => NonAlphanumeric().Replace(folder.TrimEnd('\\', '/'), "-");

    /// <summary>The newest sessions of <paramref name="folder"/> written since <paramref name="since"/>, newest first.</summary>
    public static IReadOnlyList<(string SessionId, string TranscriptPath, DateTimeOffset Written)> RecentSessions(
        string folder, DateTimeOffset since, int max, string? root = null)
    {
        var key = ProjectKey(folder);
        var dir = root ?? DefaultRoot();
        try
        {
            if (!Directory.Exists(dir)) return [];
            // Claude has written the drive letter both ways over the years ("C--x" and "c--x").
            return Directory.EnumerateDirectories(dir)
                .Where(d => string.Equals(Path.GetFileName(d), key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl"))
                .Select(f => (SessionId: Path.GetFileNameWithoutExtension(f), TranscriptPath: f, Written: new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
                .Where(s => s.Written >= since && Guid.TryParse(s.SessionId, out _))
                .OrderByDescending(s => s.Written)
                .Take(max)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
