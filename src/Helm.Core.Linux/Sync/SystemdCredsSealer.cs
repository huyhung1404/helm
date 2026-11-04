using System.Diagnostics;
using System.Security.Cryptography;
using Helm.Core.Processes;

namespace Helm.Core.Sync;

/// <summary>Seals the host key so the disk alone does not give it away (<see cref="FileKeySecretProtector"/>).</summary>
public interface IHostKeySealer
{
    /// <summary>Why a new key cannot be sealed on this box, or null.</summary>
    string? Unavailable();

    /// <exception cref="CryptographicException">Sealing failed.</exception>
    byte[] Seal(byte[] key);

    /// <exception cref="CryptographicException">The sealed key does not open here (another box, another TPM, no rights).</exception>
    byte[] Unseal(byte[] sealedKey);
}

/// <summary>
/// <c>systemd-creds encrypt/decrypt</c> under the credential name <see cref="FileKeySecretProtector.CredentialName"/>,
/// so a service can load the same file with <c>LoadCredentialEncrypted=</c>. <paramref name="withKey"/> is what
/// <c>--with-key</c> takes: <c>tpm2</c> (the TPM only), <c>host</c> (/var/lib/systemd/credential.secret, root only) or
/// <c>auto</c>. Sealing with the host key needs root; the TPM needs access to /dev/tpmrm0.
/// </summary>
public sealed class SystemdCredsSealer(string withKey = "tpm2", SystemdCredsSealer.Runner? run = null) : IHostKeySealer
{
    /// <summary>Runs systemd-creds with these arguments and this input: its exit code, output and error text.</summary>
    public delegate (int Exit, byte[] Output, string Error) Runner(IReadOnlyList<string> args, byte[] input);

    private readonly Runner _run = run ?? RunProcess;
    private readonly bool _real = run is null;

    public string? Unavailable()
    {
        if (_real && CommandFinder.OnPath("systemd-creds") is null) return "systemd-creds is not installed";
        if (withKey != "tpm2") return null;
        // has-tpm2 exits 0 only when the firmware, the kernel driver and systemd all support the TPM2.
        var (exit, _, _) = _run(["has-tpm2", "--quiet"], []);
        return exit == 0 ? null : "this box has no usable TPM2";
    }

    public byte[] Seal(byte[] key) => Run(["encrypt", $"--with-key={withKey}", $"--name={FileKeySecretProtector.CredentialName}", "-", "-"], key, "seal");

    public byte[] Unseal(byte[] sealedKey) => Run(["decrypt", $"--name={FileKeySecretProtector.CredentialName}", "-", "-"], sealedKey, "unseal");

    private byte[] Run(string[] args, byte[] input, string what)
    {
        var (exit, output, error) = _run(args, input);
        if (exit != 0 || output.Length == 0)
            throw new CryptographicException($"systemd-creds could not {what} the host key: {(error.Trim() is { Length: > 0 } e ? e : $"exit code {exit}")}");
        return output;
    }

    private static (int Exit, byte[] Output, string Error) RunProcess(IReadOnlyList<string> args, byte[] input)
    {
        var info = new ProcessStartInfo("systemd-creds")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info) ?? throw new CryptographicException("systemd-creds did not start.");
            var error = process.StandardError.ReadToEndAsync();
            var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            process.StandardInput.BaseStream.Write(input);
            process.StandardInput.Close();
            copy.Wait();
            process.WaitForExit();
            return (process.ExitCode, output.ToArray(), error.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, [], ex.Message);
        }
    }
}
