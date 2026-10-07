using System.Globalization;
using System.Text.Json.Serialization;

namespace Helm.Modules.Ssh;

public static class SshIds
{
    /// <summary>Module id on both apps: the settings file (ssh.json) and the enabled-state key.</summary>
    public const string ModuleId = "ssh";

    public const string DisplayName = "SSH";

    public const string Description = "Open a terminal on your servers over SSH, signed in with a key made for this device.";
}

/// <summary>How Helm signs in to a server.</summary>
public enum SshAuthKind
{
    /// <summary>This device's own Ed25519 key (<see cref="SshDeviceKey"/>), once it is in the server's authorized_keys.</summary>
    DeviceKey,

    /// <summary>A password typed each time; Helm never stores it.</summary>
    Password,

    /// <summary>
    /// A private key file already on this device (e.g. ~/.ssh/id_rsa, used by the ssh command). Helm keeps only its
    /// path and reads it when connecting; a passphrase, if the key has one, is typed each time.
    /// </summary>
    KeyFile,

    /// <summary>
    /// A field of a Vault item: a password, or a private key pasted there. Helm keeps only which item and field, reads
    /// the value when connecting (the vault must be unlocked) and never stores a copy.
    /// </summary>
    Vault,
}

/// <summary>A server in the list (stored in settings/ssh.json on this device).</summary>
public sealed record SshHost
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>May be empty: the list then shows <see cref="Target"/>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Host name or IP address.</summary>
    public string Address { get; init; } = "";

    public int Port { get; init; } = SshAddress.DefaultPort;

    public string User { get; init; } = "";

    public SshAuthKind Auth { get; init; } = SshAuthKind.DeviceKey;

    /// <summary>With <see cref="SshAuthKind.KeyFile"/>: the private key's full path.</summary>
    public string? KeyFile { get; init; }

    /// <summary>With <see cref="SshAuthKind.Vault"/>: the item's id, its title when chosen (for display) and the field.</summary>
    public string? VaultItemUid { get; init; }

    public string? VaultItemTitle { get; init; }

    public string? VaultField { get; init; }

    /// <summary>The server's menu program; null means <see cref="SshMenu.DefaultPath"/>.</summary>
    public string? MenuPath { get; init; }

    /// <summary>AI agents (MCP) may use this server's menu: off unless turned on for this server.</summary>
    public bool AllowMcp { get; init; }

    /// <summary>user@host, with :port when it is not 22.</summary>
    [JsonIgnore]
    public string Target => SshAddress.Format(User, Address, Port);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Target : Name.Trim();
}

/// <summary>A server key this device has accepted (like a line of OpenSSH's known_hosts).</summary>
/// <param name="Fingerprint">SHA-256 of the key, base64 without padding (OpenSSH's "SHA256:…" without the prefix).</param>
public sealed record SshKnownHost(string Address, int Port, string Algorithm, string Fingerprint, DateTimeOffset AddedAt)
{
    [JsonIgnore]
    public string Endpoint => Port == SshAddress.DefaultPort ? Address : $"{Address}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Reads and writes the "user@host:port" form people type.</summary>
public static class SshAddress
{
    public const int DefaultPort = 22;

    /// <summary>
    /// Parses <c>user@host</c>, <c>user@host:port</c>, <c>user@[v6]:port</c> or <c>user@v6</c>. The user is required:
    /// Helm has no local account name to fall back on.
    /// </summary>
    public static bool TryParse(string? text, out string user, out string host, out int port)
    {
        user = host = "";
        port = DefaultPort;
        var s = (text ?? "").Trim();
        if (s.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)) s = s[4..].Trim();
        var at = s.LastIndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        user = s[..at];
        var rest = s[(at + 1)..];
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 2) return false;
            host = rest[1..close];
            var after = rest[(close + 1)..];
            if (after.Length > 0 && (!after.StartsWith(':') || !TryPort(after[1..], out port))) return false;
        }
        else
        {
            var colon = rest.IndexOf(':');
            // More than one colon and no brackets: a bare IPv6 address without a port.
            if (colon >= 0 && rest.IndexOf(':', colon + 1) < 0)
            {
                if (!TryPort(rest[(colon + 1)..], out port)) return false;
                host = rest[..colon];
            }
            else host = rest;
        }
        return IsValidUser(user) && IsValidHost(host);
    }

    public static string Format(string user, string host, int port)
    {
        var h = host.Contains(':') && port != DefaultPort ? $"[{host}]" : host;
        return port == DefaultPort ? $"{user}@{h}" : $"{user}@{h}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static bool IsValidUser(string user) =>
        user.Length is > 0 and <= 64 && user.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('@' or '\'' or '"' or '`'));

    internal static bool IsValidHost(string host) =>
        host.Length is > 0 and <= 253 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '%');

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}
