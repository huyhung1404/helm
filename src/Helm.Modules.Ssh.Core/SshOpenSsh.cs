using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Helm.Modules.Ssh;

/// <summary>A Host block of an OpenSSH config file, as Helm uses it.</summary>
public sealed record SshConfigHost(string Alias, string Address, int Port, string User, string? IdentityFile);

/// <summary>
/// Reads what OpenSSH already knows on this device (~/.ssh/config and ~/.ssh/known_hosts), so servers set up for the
/// ssh command work in Helm with one click: the same address, user, port and key file, and the server keys already
/// trusted there. Only file paths are kept; private keys are never copied.
/// </summary>
public static class SshOpenSsh
{
    /// <summary>The keys OpenSSH tries when a Host sets no IdentityFile, in its order.</summary>
    private static readonly string[] DefaultKeys = ["id_rsa", "id_ecdsa", "id_ed25519"];

    /// <summary>
    /// The concrete Host blocks of a config file (patterns such as <c>*</c> and Match blocks are skipped). Keys are
    /// case-insensitive, values may be quoted, and <c>~</c> in IdentityFile is the home folder.
    /// </summary>
    public static List<SshConfigHost> ParseConfig(string text, string home, string? defaultUser = null)
    {
        var hosts = new List<SshConfigHost>();
        string? alias = null;
        string? address = null, user = null, identity = null;
        var port = SshAddress.DefaultPort;
        var inMatch = false;

        void Flush()
        {
            if (alias is not null && !inMatch)
            {
                var host = address ?? alias;
                var who = user ?? defaultUser;
                if (who is not null && SshAddress.IsValidUser(who) && SshAddress.IsValidHost(host))
                    hosts.Add(new SshConfigHost(alias, host, port, who, identity));
            }
            alias = address = user = identity = null;
            port = SshAddress.DefaultPort;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var (key, value) = SplitKeyword(line);
            switch (key.ToLowerInvariant())
            {
                case "host":
                    Flush();
                    inMatch = false;
                    alias = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(a => a.IndexOfAny(['*', '?', '!']) < 0);
                    break;
                case "match":
                    Flush();
                    inMatch = true;
                    break;
                case "hostname" when alias is not null:
                    address = value;
                    break;
                case "user" when alias is not null:
                    user = value;
                    break;
                case "port" when alias is not null:
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535) port = p;
                    break;
                case "identityfile" when alias is not null:
                    identity ??= ExpandHome(value, home);
                    break;
            }
        }
        Flush();
        return hosts;
    }

    /// <summary>The key file OpenSSH would use for <paramref name="host"/>: its IdentityFile, or the first default key that exists.</summary>
    public static string? KeyFileFor(SshConfigHost host, string sshFolder) =>
        host.IdentityFile is { } file ? file : DefaultKeys.Select(k => Path.Combine(sshFolder, k)).FirstOrDefault(File.Exists);

    /// <summary>
    /// The keys known_hosts lists for this server (plain or hashed names, "[host]:port" for other ports than 22).
    /// Revoked keys and certificate authorities are left out.
    /// </summary>
    public static List<SshHostKey> KnownKeys(string knownHostsText, string address, int port)
    {
        var name = port == SshAddress.DefaultPort ? address : $"[{address}]:{port.ToString(CultureInfo.InvariantCulture)}";
        var keys = new List<SshHostKey>();
        foreach (var raw in knownHostsText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('@')) continue;
            var parts = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !NameMatches(parts[0], name)) continue;
            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException)
            {
                continue;
            }
            if (BlobType(blob) != parts[1]) continue;
            keys.Add(new SshHostKey(address, port, SshKnownHosts.KeyType(parts[1]), Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=')));
        }
        return keys;
    }

    private static bool NameMatches(string field, string name)
    {
        foreach (var entry in field.Split(','))
        {
            if (entry.StartsWith("|1|", StringComparison.Ordinal))
            {
                // Hashed: |1|base64(salt)|base64(HMAC-SHA1(salt, name)).
                var bits = entry.Split('|');
                if (bits.Length != 4) continue;
                try
                {
                    var hash = HMACSHA1.HashData(Convert.FromBase64String(bits[2]), Encoding.ASCII.GetBytes(name));
                    if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(bits[3]))) return true;
                }
                catch (FormatException)
                {
                    // Not a hashed name after all.
                }
            }
            else if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The key type written at the start of an SSH public key blob.</summary>
    private static string? BlobType(byte[] blob)
    {
        if (blob.Length < 4) return null;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob);
        return length is > 0 and < 64 && blob.Length >= 4 + length ? Encoding.ASCII.GetString(blob, 4, length) : null;
    }

    private static (string Key, string Value) SplitKeyword(string line)
    {
        var split = line.IndexOfAny([' ', '\t', '=']);
        if (split < 0) return (line, "");
        var value = line[(split + 1)..].TrimStart(' ', '\t', '=').Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return (line[..split], value);
    }

    private static string ExpandHome(string path, string home)
    {
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) path = Path.Combine(home, path[2..]);
        return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }
}
