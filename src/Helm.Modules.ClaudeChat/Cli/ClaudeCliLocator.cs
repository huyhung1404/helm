using System.Text.RegularExpressions;
using Helm.Core.Processes;

namespace Helm.Modules.ClaudeChat.Cli;

/// <summary>An installed Claude Code CLI: the native <c>claude.exe</c>, or the npm shim <c>claude.cmd</c> run through cmd.exe.</summary>
public sealed record ClaudeCli(string Path)
{
    public bool IsBatchScript => Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full command line for <paramref name="args"/>. A .cmd shim needs <c>cmd.exe /d /s /c "..."</c>, where cmd
    /// itself interprets &amp; | &lt; &gt; ^ % — so arguments containing those are rejected rather than escaped.
    /// </summary>
    public string BuildCommandLine(IReadOnlyList<string> args)
    {
        if (!IsBatchScript) return CommandLine.Join([Path, .. args]);

        foreach (var arg in args)
        {
            if (!ClaudeCliLocator.IsCmdSafe(arg)) throw new ArgumentException($"The argument '{arg}' cannot be passed safely through cmd.exe.", nameof(args));
        }
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe";
        return $"{CommandLine.Quote(comSpec)} /d /s /c \"{CommandLine.Join([Path, .. args])}\"";
    }
}

public static partial class ClaudeCliLocator
{
    /// <summary>
    /// Finds the CLI: <paramref name="overridePath"/> when set, else the first match on PATH — the same one typing
    /// <c>claude</c> in a terminal runs (claude.exe before claude.cmd within a folder) — else the native installer's
    /// location. PATH wins because a machine can hold a stale native install next to a current npm one.
    /// Returns null when Claude Code is not installed.
    /// </summary>
    public static ClaudeCli? Find(string? overridePath = null, string? pathVariable = null, string? userProfile = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? new ClaudeCli(System.IO.Path.GetFullPath(overridePath)) : null;

        var dirs = (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'));
        foreach (var dir in dirs)
        {
            foreach (var name in (string[])["claude.exe", "claude.cmd"])
            {
                string candidate;
                try
                {
                    candidate = System.IO.Path.Combine(dir, name);
                }
                catch (ArgumentException)
                {
                    continue; // malformed PATH entry
                }
                if (File.Exists(candidate)) return new ClaudeCli(candidate);
            }
        }

        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = System.IO.Path.Combine(userProfile, ".local", "bin", "claude.exe");
        return File.Exists(native) ? new ClaudeCli(native) : null;
    }

    internal static bool IsCmdSafe(string arg) => CmdSafe().IsMatch(arg);

    [GeneratedRegex(@"^[A-Za-z0-9 ._:=,/\\@+\-\[\]]*$")]
    private static partial Regex CmdSafe();
}
