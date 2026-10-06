using Renci.SshNet;

namespace Helm.Modules.Ssh;

/// <summary>How Helm opens a connection: sign-in methods and the algorithms it accepts.</summary>
public static class SshConnection
{
    /// <summary>
    /// Builds the connection for <paramref name="host"/>. With a password, both "password" and "keyboard-interactive"
    /// are offered (many servers ask for the password through the second). The password is only held by SSH.NET for
    /// this connection; Helm never writes it anywhere.
    /// </summary>
    internal static ConnectionInfo Create(SshHost host, SshDeviceKey deviceKey, string? password)
    {
        AuthenticationMethod[] methods;
        if (host.Auth == SshAuthKind.Password)
        {
            var keyboard = new KeyboardInteractiveAuthenticationMethod(host.User);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                // Only hidden prompts get the password; a visible one (a question) is answered with nothing.
                foreach (var prompt in e.Prompts) prompt.Response = prompt.IsEchoed ? "" : password ?? "";
            };
            methods = [new PasswordAuthenticationMethod(host.User, password ?? ""), keyboard];
        }
        else methods = [new PrivateKeyAuthenticationMethod(host.User, deviceKey.CreateKeySource())];
        var info = new ConnectionInfo(host.Address, host.Port, host.User, methods);
        Harden(info);
        return info;
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
