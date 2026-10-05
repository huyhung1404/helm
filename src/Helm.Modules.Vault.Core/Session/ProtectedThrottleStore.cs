using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Sync;

namespace Helm.Modules.Vault.Session;

/// <summary>One read of the throttle file: whether it was missing, whether it was there but unreadable (tampered), and the stored counts.</summary>
internal readonly record struct ThrottleRead(bool FileMissing, bool Tampered, int Failed, long LastFailedMs);

/// <summary>
/// The brute-force throttle counter (<see cref="VaultDeviceState.FailedPasswordAttempts"/> and its timestamp), kept in a
/// small file protected by <see cref="ISecretProtector"/> (DPAPI on Windows, the Android Keystore on a phone) so it
/// cannot be reset by editing a plaintext settings file. Written synchronously and atomically on every change, so a
/// crash cannot lose a just-recorded failure. The caller passes the time in, so it never reads the wall clock itself.
/// </summary>
internal sealed class ProtectedThrottleStore(string filePath, ISecretProtector protector)
{
    private const string Purpose = "helm-vault/throttle.v1";

    public string FilePath { get; } = filePath;

    /// <summary>Never throws: classifies the file as missing, tampered (present but unreadable), or good.</summary>
    public ThrottleRead Load()
    {
        if (!File.Exists(FilePath)) return new ThrottleRead(FileMissing: true, Tampered: false, 0, 0);
        try
        {
            var state = JsonSerializer.Deserialize<State>(protector.Unprotect(File.ReadAllBytes(FilePath), Purpose));
            if (state is null) return new ThrottleRead(false, Tampered: true, 0, 0);
            return new ThrottleRead(false, false, Math.Max(0, state.Failed), Math.Max(0, state.LastFailedMs));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return new ThrottleRead(false, Tampered: true, 0, 0);
        }
    }

    /// <summary>Never throws out: a failed write costs a lost update, never the unlock flow.</summary>
    public void Save(int failed, long lastFailedMs)
    {
        try
        {
            var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new State(Math.Max(0, failed), lastFailedMs)), Purpose);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Best effort: the next failure (or a successful unlock) writes it again.
        }
    }

    private sealed record State(int Failed, long LastFailedMs);
}
