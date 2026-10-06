namespace Helm.Modules.Ssh;

/// <summary>Adds this device's public key to the server's ~/.ssh/authorized_keys, like ssh-copy-id.</summary>
public static class SshKeyInstall
{
    /// <summary>
    /// A POSIX shell command that adds <paramref name="publicKeyLine"/> once: it creates ~/.ssh (700) and the file (600)
    /// if needed, ends the last line before appending, and does nothing when the line is already there.
    /// </summary>
    /// <exception cref="ArgumentException">The line is not a plain one-line public key (it could break the quoting).</exception>
    public static string Command(string publicKeyLine)
    {
        var line = publicKeyLine.Trim();
        if (line.Length == 0 || line.Any(c => c is '\'' or '\\' or '\r' or '\n' or '`' or '$' || char.IsControl(c)) || !line.StartsWith("ssh-", StringComparison.Ordinal))
            throw new ArgumentException("Not a one-line SSH public key.", nameof(publicKeyLine));
        return "umask 077; mkdir -p ~/.ssh && f=~/.ssh/authorized_keys && touch \"$f\" && chmod 600 \"$f\" && "
               + "{ [ ! -s \"$f\" ] || [ -z \"$(tail -c1 \"$f\")\" ] || echo >> \"$f\"; } && "
               + $"{{ grep -qxF '{line}' \"$f\" || printf '%s\\n' '{line}' >> \"$f\"; }}";
    }
}
