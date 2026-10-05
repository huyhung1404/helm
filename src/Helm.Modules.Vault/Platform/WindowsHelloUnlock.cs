using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.Logging;
using Windows.Security.Credentials;

namespace Helm.Modules.Vault.Platform;

/// <summary>
/// Quick unlock with Windows Hello (face, fingerprint or PIN). Windows keeps a private key that it only uses after the
/// user verifies (<c>RequestSignAsync</c> shows the Windows Hello prompt on every call); Helm asks it to sign a random
/// challenge and derives the key that wraps the vault key from that signature with HKDF and a random salt (RSA PKCS#1
/// v1.5 signatures are deterministic, so the same challenge gives the same key). The wrapped key is stored under DPAPI
/// in settings/vault/hello.bin. Resetting Windows Hello or the PIN makes the signature change, so the stored key stops
/// working and the vault asks for its password; so does a provider that signs differently (fails safe).
/// </summary>
internal sealed class WindowsHelloUnlock(ISecretProtector protector, HelmPaths paths, ILogger<WindowsHelloUnlock> logger) : IVaultDeviceUnlock
{
    private const string CredentialName = "Helm Vault";
    private const string Purpose = "helm-vault/hello";
    private const string KekInfo = "helm-vault/hello/v2";
    private string FilePath => Path.Combine(paths.ModuleDataDirectory("vault"), "hello.bin");

    public string Name => "Windows Hello";

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Windows Hello is not available");
            return false;
        }
    }

    public bool IsEnrolled(string vaultId) => Load() is { } stored && stored.VaultId == vaultId;

    public async Task<bool> EnrollAsync(string vaultId, ReadOnlyMemory<byte> vaultKey, CancellationToken ct)
    {
        var created = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.ReplaceExisting);
        if (created.Status != KeyCredentialStatus.Success)
        {
            logger.LogInformation("Windows Hello enrollment ended with {Status}", created.Status);
            return false;
        }
        var challenge = RandomNumberGenerator.GetBytes(32);
        var signature = await SignAsync(created.Credential, challenge);
        if (signature is null) return false;
        try
        {
            await WriteAsync(vaultId, challenge, signature, vaultKey, ct);
            logger.LogInformation("Windows Hello quick unlock turned on");
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public async Task<byte[]?> UnlockAsync(string vaultId, CancellationToken ct)
    {
        var stored = Load();
        if (stored is null || stored.VaultId != vaultId) throw new VaultDeviceUnlockException("Windows Hello is not set up for this vault.");
        var opened = await KeyCredentialManager.OpenAsync(CredentialName);
        if (opened.Status == KeyCredentialStatus.NotFound) throw new VaultDeviceUnlockException("The Windows Hello key for Vault is gone (Windows Hello was reset).");
        if (opened.Status != KeyCredentialStatus.Success) return null;
        var signature = await SignAsync(opened.Credential, stored.Challenge);
        if (signature is null) return null;
        try
        {
            byte[] key;
            var kek = KekOf(signature, stored.Salt);
            try
            {
                key = VaultCrypto.Open(kek, stored.Wrapped, Aad(vaultId));
            }
            catch (CryptographicException ex)
            {
                throw new VaultDeviceUnlockException("Windows Hello changed since quick unlock was turned on. Unlock with the password and turn it on again.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
            if (stored.Salt is null)
            {
                // A file from before the salted key: wrap again now, with the same challenge and this signature (no new prompt).
                try
                {
                    await WriteAsync(vaultId, stored.Challenge, signature, key, ct);
                    logger.LogInformation("Windows Hello quick unlock file upgraded");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    logger.LogWarning(ex, "Could not upgrade the Windows Hello quick unlock file; it keeps working as it is");
                }
            }
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    /// <summary>
    /// The key that wraps the vault key: HKDF-SHA256 of the Windows Hello signature with the file's random salt. Files
    /// written before the salt (no <paramref name="salt"/>) used SHA-256 of the purpose and the signature, and still open.
    /// The caller zeroes it.
    /// </summary>
    internal static byte[] KekOf(ReadOnlySpan<byte> signature, byte[]? salt)
    {
        if (salt is null) return SHA256.HashData([.. Encoding.UTF8.GetBytes(Purpose + "|"), .. signature]);
        var kek = new byte[VaultCrypto.KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, kek, salt, Encoding.UTF8.GetBytes(KekInfo));
        return kek;
    }

    /// <summary>Wraps the vault key with a fresh salt and writes hello.bin (atomically, under DPAPI).</summary>
    private async Task WriteAsync(string vaultId, byte[] challenge, byte[] signature, ReadOnlyMemory<byte> vaultKey, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var kek = KekOf(signature, salt);
        try
        {
            var stored = new Stored(vaultId, challenge, VaultCrypto.Seal(kek, vaultKey.Span, Aad(vaultId)), salt);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            await File.WriteAllBytesAsync(temp, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(stored), Purpose), ct);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public void Remove()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            _ = KeyCredentialManager.DeleteAsync(CredentialName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove the Windows Hello key");
        }
    }

    /// <returns>The signature (the caller zeroes it), or null when the user cancelled.</returns>
    private async Task<byte[]?> SignAsync(KeyCredential credential, byte[] challenge)
    {
        var signed = await credential.RequestSignAsync(challenge.AsBuffer());
        if (signed.Status != KeyCredentialStatus.Success)
        {
            logger.LogInformation("Windows Hello verification ended with {Status}", signed.Status);
            return null;
        }
        return signed.Result.ToArray();
    }

    private Stored? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return Parse(protector.Unprotect(File.ReadAllBytes(FilePath), Purpose));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            logger.LogWarning(ex, "The Windows Hello quick unlock file is unreadable");
            return null;
        }
    }

    private static string Aad(string vaultId) => $"helm-vault/v1/hello|{vaultId}";

    internal static Stored? Parse(byte[] json) => JsonSerializer.Deserialize<Stored>(json);

    /// <param name="Salt">The HKDF salt of <see cref="KekOf"/>; null in files written before it.</param>
    internal sealed record Stored(string VaultId, byte[] Challenge, byte[] Wrapped, byte[]? Salt = null);
}
