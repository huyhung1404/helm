using System.Text.RegularExpressions;

namespace Helm.Modules.Ssh;

/// <summary>
/// Checks for AI agents' shell commands (ssh_exec): the length limit, which commands are plain status reads that may
/// run without asking (<see cref="SshHost.AllowMcpStatusWithoutAsking"/>), and which are still asked on a server that
/// runs commands without asking (<see cref="SshHost.AllowMcpWithoutAsking"/>).
/// </summary>
public static partial class SshShellCommands
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

    /// <summary>
    /// Why a command is still asked on a server that runs AI agents' commands without asking, or null: it can wipe a
    /// disk, stop the server, change its users, SSH access or firewall, delete a whole folder such as / or ~, Docker
    /// volumes or a database, or run a script piped into a shell. A net for a slip or an injected command, not a
    /// sandbox: a command written to hide from it can.
    /// </summary>
    public static string? AlwaysAsked(string command)
    {
        if (ForkBomb().IsMatch(command)) return "is a fork bomb";
        if (PipedIntoShell().IsMatch(command)) return "runs a script piped into a shell (… | sh)";
        if (AccessFiles().IsMatch(command)) return "touches SSH access, users or sudo rights";
        if (DropsData().IsMatch(command)) return "deletes a database";
        foreach (var words in Programs(command))
            if (AlwaysAsked(words[0], words[1..]) is { } why) return why;
        return null;
    }

    private static string? AlwaysAsked(string program, string[] args)
    {
        switch (program)
        {
            case "sh" or "bash" or "zsh" or "dash" or "ksh" when args.Contains("-c"):
                return AlwaysAsked(string.Join(' ', args.SkipWhile(a => a != "-c").Skip(1)));
            case "mkfs" or "wipefs" or "fdisk" or "sfdisk" or "parted" or "shred" or "blkdiscard":
            case not null when program.StartsWith("mkfs.", StringComparison.Ordinal):
                return "can wipe a disk";
            case "dd" when args.Any(a => a.StartsWith("of=/dev/", StringComparison.Ordinal)):
                return "writes straight to a disk";
            case "shutdown" or "reboot" or "halt" or "poweroff":
            case "init" or "telinit" when args is ["0" or "6", ..]:
            case "systemctl" when args.Any(a => a is "reboot" or "poweroff" or "halt" or "kexec" or "rescue" or "emergency"):
                return "stops or restarts the server";
            case "useradd" or "adduser" or "userdel" or "deluser" or "usermod" or "passwd" or "chpasswd" or "visudo":
                return "changes the server's users or passwords";
            case "iptables" or "ip6tables" when args.Any(a => a is "-F" or "--flush" or "-X" or "--delete-chain" or "-P" or "--policy"):
            case "ufw" when args is ["disable" or "reset" or "deny" or "default", ..]:
            case "nft" when args is ["flush" or "delete", ..]:
                return "changes the firewall";
            case "rm" when args.Contains("--no-preserve-root") || args.Any(Recursive) && args.Any(Whole):
            // find … -delete with a name or path filter only deletes what matches.
            case "find" when args.Contains("-delete") && args.TakeWhile(a => !a.StartsWith('-')).Any(Whole)
                             && !args.Any(a => a is "-name" or "-iname" or "-path" or "-ipath" or "-regex" or "-iregex"):
                return "deletes a whole folder such as / or ~";
            case "chmod" or "chown" or "chgrp" when args.Any(Recursive) && args.Any(Whole):
                return "changes the owner or rights of a whole folder such as / or ~";
            case "docker" when args is ["volume", "rm" or "prune", ..]
                               || args is ["system", "prune", ..] && args.Contains("--volumes")
                               || args is ["compose", .. var compose] && DownWithVolumes(compose):
            case "docker-compose" when DownWithVolumes(args):
                return "deletes Docker volumes (their data)";
            case "crontab" when args.Contains("-r"):
                return "deletes the user's crontab";
            default:
                return null;
        }
    }

    /// <summary>
    /// Each program the command runs, as its name (without a folder) and its arguments: split at ; &amp; | ( ) ` $( and line
    /// breaks, past sudo, env, nohup, timeout… and VAR=value, with quotes and backslashes taken out (so r''m is rm).
    /// </summary>
    private static IEnumerable<string[]> Programs(string command)
    {
        foreach (var part in Separators().Split(command))
        {
            var words = part.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Replace("'", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal))
                .Where(w => w.Length > 0)
                .ToList();
            var i = 0;
            while (i < words.Count)
            {
                if (Assignment().IsMatch(words[i]))
                {
                    i++;
                    continue;
                }
                var prefix = Name(words[i]);
                if (prefix is not ("sudo" or "doas" or "env" or "nohup" or "exec" or "time" or "command" or "nice" or "ionice" or "timeout" or "xargs" or "{" or "!")) break;
                i++;
                while (i < words.Count && words[i].StartsWith('-')) i += words[i] is "-u" or "-g" or "-n" or "-s" or "-k" or "-c" ? 2 : 1;
                if (prefix == "timeout" && i < words.Count && char.IsAsciiDigit(words[i][0])) i++;
            }
            if (i < words.Count) yield return [Name(words[i]), .. words.Skip(i + 1)];
        }
    }

    private static string Name(string word) => word[(word.LastIndexOf('/') + 1)..];

    private static bool Recursive(string arg) =>
        arg == "--recursive" || arg.Length > 1 && arg[0] == '-' && arg[1] != '-' && (arg.Contains('r') || arg.Contains('R'));

    /// <summary>
    /// The root, the home folder, the folder the command runs in, or a folder one or two levels under the root (/etc,
    /// /var/www, /home/deploy; not /tmp/…), or a database's folder under /var/lib.
    /// </summary>
    private static bool Whole(string path)
    {
        if (path is "/" or "/*" or "~" or "~/" or "~/*" or "$HOME" or "$HOME/" or "$HOME/*" or "${HOME}" or "${HOME}/" or "*" or "." or "./" or "./*" or ".." or "../")
            return true;
        if (!path.StartsWith('/') || path.StartsWith("/tmp/", StringComparison.Ordinal)) return false;
        var trimmed = path.TrimEnd('*').TrimEnd('/');
        return trimmed.Count(c => c == '/') <= 2 || VarLibFolder().IsMatch(trimmed);
    }

    private static bool DownWithVolumes(string[] args) => args.Contains("down") && args.Any(a => a is "-v" or "--volumes");

    [GeneratedRegex(@"\$\(|[;&|()`\r\n]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^/var/lib/[^/]+$")]
    private static partial Regex VarLibFolder();

    [GeneratedRegex(@":\s*\(\s*\)\s*\{[^}]*:\s*\|\s*:")]
    private static partial Regex ForkBomb();

    // | sh, | sudo bash, | /bin/zsh… and sh <(curl …), source <(…).
    [GeneratedRegex(@"\|\s*(?:sudo\s+(?:-\S+\s+)*)?(?:\S*/)?(?:ba|z|da|k)?sh(?:\s|;|$)|(?:^|[\s;&|(])(?:(?:ba|z|da|k)?sh|source|\.)\s+<\(")]
    private static partial Regex PipedIntoShell();

    [GeneratedRegex(@"authorized_keys|/etc/ssh/|sshd_config|/etc/sudoers|/etc/shadow|/etc/passwd\b|/etc/group\b")]
    private static partial Regex AccessFiles();

    [GeneratedRegex(@"\bdrop\s+(?:database|schema|table)\b|\btruncate\s+table\b|\bdropdb\b|\bflush(?:all|db)\b|\.dropDatabase\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex DropsData();
}
