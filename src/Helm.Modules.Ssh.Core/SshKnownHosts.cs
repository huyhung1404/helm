namespace Helm.Modules.Ssh;

/// <summary>What this device knows about the key a server showed.</summary>
public enum HostKeyMatch
{
    /// <summary>Accepted before: connect.</summary>
    Trusted,

    /// <summary>First visit (or only keys of another type are known): ask before connecting.</summary>
    Unknown,

    /// <summary>A key of the same type was accepted before and this one is different: refuse.</summary>
    Changed,
}

/// <summary>The key a server showed while connecting.</summary>
/// <param name="Fingerprint">SHA-256, base64 without padding.</param>
public sealed record SshHostKey(string Address, int Port, string Algorithm, string Fingerprint)
{
    /// <summary>As OpenSSH prints it: "SHA256:…".</summary>
    public string DisplayFingerprint => "SHA256:" + Fingerprint;
}

/// <summary>Trust on first use, like OpenSSH's known_hosts: a server's key is pinned the first time it is accepted.</summary>
public static class SshKnownHosts
{
    public static HostKeyMatch Check(IEnumerable<SshKnownHost> known, SshHostKey key)
    {
        var sameEndpoint = known.Where(k => k.Port == key.Port && string.Equals(k.Address, key.Address, StringComparison.OrdinalIgnoreCase)).ToList();
        var sameType = sameEndpoint.Where(k => k.Algorithm == key.Algorithm).ToList();
        if (sameType.Count == 0) return HostKeyMatch.Unknown;
        return sameType.Any(k => k.Fingerprint == key.Fingerprint) ? HostKeyMatch.Trusted : HostKeyMatch.Changed;
    }

    /// <summary>The list with <paramref name="key"/> pinned, replacing any key of the same type for that server.</summary>
    public static List<SshKnownHost> Trust(IEnumerable<SshKnownHost> known, SshHostKey key, DateTimeOffset now) =>
    [
        .. known.Where(k => !(k.Port == key.Port && k.Algorithm == key.Algorithm && string.Equals(k.Address, key.Address, StringComparison.OrdinalIgnoreCase))),
        new SshKnownHost(key.Address, key.Port, key.Algorithm, key.Fingerprint, now),
    ];

    /// <summary>
    /// The key's type from the host key algorithm the session agreed on: an RSA key signs as rsa-sha2-256 or -512 but
    /// is one key, and a certificate pins the key inside it.
    /// </summary>
    public static string KeyType(string hostKeyAlgorithm)
    {
        const string cert = "-cert-v01@openssh.com";
        var name = hostKeyAlgorithm.EndsWith(cert, StringComparison.Ordinal) ? hostKeyAlgorithm[..^cert.Length] : hostKeyAlgorithm;
        return name is "rsa-sha2-256" or "rsa-sha2-512" ? "ssh-rsa" : name;
    }

    /// <summary>The server's key file for a key type, to check a fingerprint with ssh-keygen -lf.</summary>
    public static string ServerKeyFile(string keyType) => keyType switch
    {
        "ssh-ed25519" => "/etc/ssh/ssh_host_ed25519_key.pub",
        "ssh-rsa" => "/etc/ssh/ssh_host_rsa_key.pub",
        _ when keyType.StartsWith("ecdsa", StringComparison.Ordinal) => "/etc/ssh/ssh_host_ecdsa_key.pub",
        _ => "/etc/ssh/ssh_host_*_key.pub",
    };

    /// <summary>OpenSSH writes SHA256 fingerprints as base64 without padding; SSH.NET may keep the padding.</summary>
    public static string NormalizeFingerprint(string fingerprint)
    {
        var f = fingerprint.Trim();
        if (f.StartsWith("SHA256:", StringComparison.Ordinal)) f = f[7..];
        return f.TrimEnd('=');
    }
}
