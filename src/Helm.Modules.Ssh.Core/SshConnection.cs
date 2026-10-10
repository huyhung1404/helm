using Renci.SshNet;

namespace Helm.Modules.Ssh;

/// <summary>How Helm opens a connection: sign-in methods and the algorithms it accepts.</summary>
public static class SshConnection
{
    /// <summary>
    /// Builds the connection for <paramref name="host"/>. <paramref name="password"/> is the password, or the key file's
    /// passphrase. With a password, both "password" and "keyboard-interactive"
    /// are offered (many servers ask for the password through the second). The password is only held by SSH.NET for
    /// this connection; Helm never writes it anywhere.
    /// </summary>
    /// <param name="vaultSecret">With <see cref="SshAuthKind.Vault"/>: the value read from Vault, a password or a private key.</param>
    internal static ConnectionInfo Create(SshHost host, SshDeviceKey deviceKey, string? password, string? vaultSecret = null)
    {
        AuthenticationMethod[] methods;
        if (host.Auth == SshAuthKind.Password) methods = PasswordMethods(host.User, password);
        else if (host.Auth == SshAuthKind.Vault)
        {
            if (string.IsNullOrEmpty(vaultSecret)) throw new ArgumentException("Nothing was read from Vault.", nameof(vaultSecret));
            if (IsPrivateKey(vaultSecret))
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vaultSecret.Trim() + "\n"));
                methods = [new PrivateKeyAuthenticationMethod(host.User, OpenKey(() => new PrivateKeyFile(stream, string.IsNullOrEmpty(password) ? null : password), password))];
            }
            else methods = PasswordMethods(host.User, vaultSecret);
        }
        else if (host.Auth == SshAuthKind.KeyFile)
        {
            if (string.IsNullOrEmpty(host.KeyFile) || !File.Exists(host.KeyFile))
                throw new FileNotFoundException($"The key file {host.KeyFile} was not found.", host.KeyFile);
            // Read now and kept only in memory for this connection; an empty passphrase means "none".
            methods = [new PrivateKeyAuthenticationMethod(host.User, OpenKey(() => new PrivateKeyFile(host.KeyFile, string.IsNullOrEmpty(password) ? null : password), password))];
        }
        else methods = [new PrivateKeyAuthenticationMethod(host.User, deviceKey.CreateKeySource())];
        var info = new ConnectionInfo(host.Address, host.Port, host.User, methods);
        Harden(info);
        return info;
    }

    /// <summary>
    /// Reads a private key. With a passphrase, a key that does not open is a wrong passphrase, however it fails: an
    /// encrypted PKCS#8 key is decrypted with AES-CBC, and wrong bytes usually fail the padding check (a CryptoException),
    /// but about one time in 256 they happen to pad correctly and the failure comes later, when the garbage is read as
    /// ASN.1 (an ArgumentException or IOException such as "failed to construct sequence from byte[]").
    /// </summary>
    private static PrivateKeyFile OpenKey(Func<PrivateKeyFile> open, string? passphrase)
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (!string.IsNullOrEmpty(passphrase)
                                   && ex is ArgumentException or InvalidOperationException or FormatException
                                       or (IOException and not FileNotFoundException and not DirectoryNotFoundException))
        {
            throw new Renci.SshNet.Common.SshException("The key could not be opened with this passphrase.", ex);
        }
    }

    /// <summary>True for a private key's text (PEM or OpenSSH): Vault may hold a key instead of a password.</summary>
    internal static bool IsPrivateKey(string secret)
    {
        var s = secret.TrimStart();
        return s.StartsWith("-----BEGIN ", StringComparison.Ordinal) && s.Contains("PRIVATE KEY-----", StringComparison.Ordinal);
    }

    private static AuthenticationMethod[] PasswordMethods(string user, string? password)
    {
        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            // Only hidden prompts get the password; a visible one (a question) is answered with nothing.
            foreach (var prompt in e.Prompts) prompt.Response = prompt.IsEchoed ? "" : password ?? "";
        };
        return [new PasswordAuthenticationMethod(user, password ?? ""), keyboard];
    }

    /// <summary>
    /// Leaves out what OpenSSH itself has retired: SHA-1 key exchange and MACs, CBC and 3DES ciphers, DSA host keys and
    /// RSA signatures with SHA-1. Any server from the last ten years still has a modern choice in common.
    /// </summary>
    internal static void Harden(ConnectionInfo info)
    {
        RemoveWhere(info.KeyExchangeAlgorithms, name => name.EndsWith("sha1", StringComparison.Ordinal) || name.Contains("group1-", StringComparison.Ordinal));
        RemoveWhere(info.Encryptions, name => name.Contains("cbc", StringComparison.Ordinal) || name.Contains("3des", StringComparison.Ordinal) || name.Contains("arcfour", StringComparison.Ordinal));
        RemoveWhere(info.HmacAlgorithms, name => name.Contains("md5", StringComparison.Ordinal) || name.StartsWith("hmac-sha1", StringComparison.Ordinal) || name.Contains("-96", StringComparison.Ordinal));
        RemoveWhere(info.HostKeyAlgorithms, name => name is "ssh-dss" or "ssh-rsa" || name.StartsWith("ssh-dss-", StringComparison.Ordinal) || name.StartsWith("ssh-rsa-cert", StringComparison.Ordinal));
    }

    private static void RemoveWhere<T>(IOrderedDictionary<string, T> algorithms, Func<string, bool> weak)
    {
        foreach (var name in algorithms.Keys.Where(weak).ToList()) algorithms.Remove(name);
    }
}
