using System.Text;

namespace Helm.Core.Processes;

/// <summary>What to start: a full command line (already quoted, see <see cref="CommandLine"/>) and its environment.</summary>
public sealed record ChildProcessStartInfo(string CommandLine, string WorkingDirectory)
{
    /// <summary>The complete environment of the child. Null inherits Helm's own environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// Starts the command-line tools Helm runs for the user (git, Claude Code, Codex, Grok) with redirected streams. On
/// Windows that is a job object and, when Helm is elevated, the desktop shell's token (Helm.Core.Windows); elsewhere a
/// plain child process (Helm.Core.Linux).
/// </summary>
public interface IChildProcessLauncher
{
    /// <summary>
    /// Starts a hidden child process with redirected stdin/stdout/stderr inside a kill-on-close job object.
    /// When Helm is elevated the child runs with the desktop shell's (unelevated) token, never as administrator.
    /// </summary>
    /// <exception cref="ChildProcessException">The process could not be started.</exception>
    ChildProcess Start(ChildProcessStartInfo startInfo);
}

public sealed class ChildProcessException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A running child process started by an <see cref="IChildProcessLauncher"/>. Disposing kills it and its whole process tree.</summary>
public abstract class ChildProcess : IDisposable
{
    public abstract int Id { get; }

    public abstract Stream StandardInput { get; }
    public abstract Stream StandardOutput { get; }
    public abstract Stream StandardError { get; }

    /// <summary>False when the process tree cannot be ended as one; then only the direct child is killed on dispose.</summary>
    public abstract bool KillsProcessTree { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    public abstract Task<int> Exited { get; }

    public bool HasExited => Exited.IsCompleted;

    public abstract void Kill();

    public abstract void Dispose();
}

/// <summary>
/// Builds Windows command lines with the quoting rules of CommandLineToArgvW / the MSVC runtime, and reads them back
/// (<see cref="Split"/>) where a process takes an argument list instead (Linux).
/// </summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal unless they precede a quote; then each must be doubled.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// The arguments of <paramref name="commandLine"/> as CommandLineToArgvW reads them: the inverse of <see cref="Join"/>
    /// (spaces and tabs separate, quotes group, 2n backslashes before a quote are n, 2n+1 are n and a literal quote).
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var run = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    run++;
                    i++;
                }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1) current.Append('"');
                    else i--; // an even run: the quote that follows opens or closes a group
                }
                else
                {
                    current.Append('\\', run);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                // "" inside a group is one literal quote (the MSVC runtime since 2008).
                if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else quoted = !quoted;
                started = true;
            }
            else if (c is ' ' or '\t' && !quoted)
            {
                if (started) args.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started) args.Add(current.ToString());
        return args;
    }
}
