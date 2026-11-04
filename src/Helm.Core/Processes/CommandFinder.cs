namespace Helm.Core.Processes;

/// <summary>
/// Where the command-line tools Helm runs are installed: the first match on PATH, as a terminal would find it. On
/// Windows that is an .exe or npm's .cmd; on Linux an executable file without an extension.
/// </summary>
public static class CommandFinder
{
    /// <summary>
    /// The Claude Code CLI typing <c>claude</c> in a terminal runs: the first claude.exe or claude.cmd on PATH, else the
    /// native installer's <c>~\.local\bin\claude.exe</c> (on Linux: <c>claude</c> on PATH, else <c>~/.local/bin/claude</c>
    /// or the old <c>~/.claude/local/claude</c>). Null when Claude Code is not installed.
    /// </summary>
    public static string? FindClaude(string? pathVariable = null, string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsWindows())
            return OnPath("claude", pathVariable)
                ?? FirstExecutable(Path.Combine(userProfile, ".local", "bin", "claude"), Path.Combine(userProfile, ".claude", "local", "claude"));

        foreach (var dir in Folders(pathVariable, ';'))
        {
            foreach (var name in (string[])["claude.exe", "claude.cmd"])
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, name);
                }
                catch (ArgumentException)
                {
                    continue; // malformed PATH entry
                }
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
        }
        var native = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        return File.Exists(native) ? native : null;
    }

    /// <summary>
    /// A command without an extension on PATH (Linux, macOS): the first file named <paramref name="name"/> that may be
    /// run. Null when there is none.
    /// </summary>
    public static string? OnPath(string name, string? pathVariable = null)
    {
        foreach (var dir in Folders(pathVariable, Path.PathSeparator))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir, name);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (Path.IsPathFullyQualified(candidate) && IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The first of <paramref name="paths"/> that is a file that may be run, or null.</summary>
    public static string? FirstExecutable(params string[] paths) => paths.FirstOrDefault(IsExecutable);

    /// <summary>A file (not a folder) that, on Linux, has an execute bit set.</summary>
    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> Folders(string? pathVariable, char separator) =>
        (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'));
}
