namespace Helm.Modules.Ssh;

/// <summary>
/// Checks for AI agents' shell commands (ssh_exec): the length limit, and which commands are plain status reads that may
/// run without asking (<see cref="SshHost.AllowMcpStatusWithoutAsking"/>).
/// </summary>
public static class SshShellCommands
{
    public const int MaxLength = 4000;

    // Letters, digits, spaces and these only: no quotes, $, `, ;, |, &, <, >, (, ), {, }, *, ?, [, ], ~, !, #, \ or line
    // breaks, so the shell can do nothing with the line but run one program with plain words.
    private const string PlainPunctuation = "-_./:=,@+%";

    /// <summary>
    /// A status command that only reads: one program from a short list, its arguments plain words, and no option that
    /// writes, sets or reaches another machine. Anything else is asked.
    /// </summary>
    public static bool IsStatusCommand(string command)
    {
        var line = command.Trim();
        if (line.Length is 0 or > 200 || !line.All(c => c == ' ' || char.IsAsciiLetterOrDigit(c) || PlainPunctuation.Contains(c))) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = words[1..];
        return words[0] switch
        {
            "uptime" or "whoami" or "id" or "uname" or "nproc" or "free" or "df" or "ps" or "ls" or "w" or "lsb_release" => true,
            "hostname" => args is [] or ["-f" or "-i" or "-I" or "-s"],
            "date" => args is [] || (args is [var format] && format.StartsWith('+')),
            "pm2" => args is ["ls" or "list" or "status"],
            "systemctl" => args is ["status" or "is-active" or "is-enabled" or "is-failed" or "list-units" or "list-timers" or "--failed", ..]
                           && args.All(a => !a.StartsWith('-') || a is "--failed" or "--all" or "--no-pager" or "--plain" or "-a" or "-l" or "--full"),
            "docker" => args is ["ps" or "images" or "version", ..] && args.Skip(1).All(a => a is "-a" or "--all" or "-q" or "--no-trunc")
                        || args is ["stats", "--no-stream"],
            "git" => args is ["status" or "log" or "describe" or "rev-parse" or "show-branch", ..] && args.All(a => !a.StartsWith("--output", StringComparison.Ordinal) && a is not ("--ext-diff" or "--textconv"))
                     || args is ["branch", ..] && args.Skip(1).All(a => a is "-a" or "-r" or "-v" or "-vv" or "--all" or "--remotes" or "--list" or "--show-current"),
            _ => false,
        };
    }
}
