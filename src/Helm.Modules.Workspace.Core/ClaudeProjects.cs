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
    public static string ProjectKey(string folder) => NonAlphanumeric().Replace(WorkspaceFolders.Native(folder).TrimEnd('\\', '/'), "-");

    /// <summary>The project folder name of a transcript (<c>…/projects/&lt;key&gt;/&lt;id&gt;.jsonl</c>); empty when it has none.</summary>
    public static string KeyOf(string transcriptPath) =>
        transcriptPath.Length == 0 ? "" : Path.GetFileName(Path.GetDirectoryName(transcriptPath)) ?? "";

    /// <summary>True when Claude keeps the sessions of <paramref name="folder"/> under <paramref name="key"/>.</summary>
    public static bool IsProjectOf(string folder, string key) =>
        key.Length > 0 && folder.Length > 0 && string.Equals(ProjectKey(folder), key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folder a session belongs to, which stays put while its working directory wanders (Claude's shell may
    /// <c>cd</c> into a worktree, a subfolder, or report <c>/c/…</c>): the folder Claude filed its transcript under.
    /// That name drops the separators, so the folder itself is found as <paramref name="cwd"/> or one of its parents, or
    /// else as the first working directory the transcript records under that name. Null when none fits.
    /// </summary>
    public static string? HomeOf(string transcriptPath, string cwd)
    {
        var key = KeyOf(transcriptPath);
        if (key.Length == 0) return null;
        for (var dir = cwd.Length > 0 ? WorkspaceFolders.Native(cwd).TrimEnd('\\') : null; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            if (IsProjectOf(dir, key)) return dir;
        return StartFolder(transcriptPath, key);
    }

    /// <summary>The first working directory a transcript records that Claude files under <paramref name="key"/>.</summary>
    internal static string? StartFolder(string transcriptPath, string key)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            for (var i = 0; i < 200 && reader.ReadLine() is { } line; i++)
            {
                if (Cwd().Match(line) is not { Success: true } m) continue;
                string? cwd;
                try
                {
                    cwd = System.Text.Json.JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\"");
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
                if (cwd is { Length: > 0 } && IsProjectOf(cwd, key)) return WorkspaceFolders.Native(cwd).TrimEnd('\\');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return null;
    }

    [GeneratedRegex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex Cwd();

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
