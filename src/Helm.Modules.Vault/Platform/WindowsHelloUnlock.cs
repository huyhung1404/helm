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
/// user verifies; Helm asks it to sign a random challenge and derives the key that wraps the vault key from that
/// signature (RSA PKCS#1 v1.5 signatures are deterministic). The wrapped key is stored under DPAPI in
/// settings/vault/hello.bin. Resetting Windows Hello or the PIN makes the signature change, so the stored key stops
/// working and the vault asks for its password.
/// </summary>
internal sealed class WindowsHelloUnlock(ISecretProtector protector, HelmPaths paths, ILogger<WindowsHelloUnlock> logger) : IVaultDeviceUnlock
{
    private const string CredentialName = "Helm Vault";
    private const string Purpose = "helm-vault/hello";
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
        var kek = await SignAsync(created.Credential, challenge);
        if (kek is null) return false;
        try
        {
            var stored = new Stored(vaultId, challenge, VaultCrypto.Seal(kek, vaultKey.Span, Aad(vaultId)));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllBytesAsync(FilePath, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(stored), Purpose), ct);
            logger.LogInformation("Windows Hello quick unlock turned on");
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<byte[]?> UnlockAsync(string vaultId, CancellationToken ct)
    {
        var stored = Load();
        if (stored is null || stored.VaultId != vaultId) throw new VaultDeviceUnlockException("Windows Hello is not set up for this vault.");
        var opened = await KeyCredentialManager.OpenAsync(CredentialName);
        if (opened.Status == KeyCredentialStatus.NotFound) throw new VaultDeviceUnlockException("The Windows Hello key for Vault is gone (Windows Hello was reset).");
        if (opened.Status != KeyCredentialStatus.Success) return null;
        var kek = await SignAsync(opened.Credential, stored.Challenge);
        if (kek is null) return null;
        try
        {
            return VaultCrypto.Open(kek, stored.Wrapped, Aad(vaultId));
        }
        catch (CryptographicException ex)
        {
            throw new VaultDeviceUnlockException("Windows Hello changed since quick unlock was turned on. Unlock with the password and turn it on again.", ex);
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

    /// <returns>The key derived from the signature, or null when the user cancelled.</returns>
    private async Task<byte[]?> SignAsync(KeyCredential credential, byte[] challenge)
    {
        var signed = await credential.RequestSignAsync(challenge.AsBuffer());
        if (signed.Status != KeyCredentialStatus.Success)
        {
            logger.LogInformation("Windows Hello verification ended with {Status}", signed.Status);
            return null;
        }
        var signature = signed.Result.ToArray();
        try
        {
            return SHA256.HashData([.. Encoding.UTF8.GetBytes(Purpose + "|"), .. signature]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private Stored? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<Stored>(protector.Unprotect(File.ReadAllBytes(FilePath), Purpose));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            logger.LogWarning(ex, "The Windows Hello quick unlock file is unreadable");
            return null;
        }
    }

    private static string Aad(string vaultId) => $"helm-vault/v1/hello|{vaultId}";

    private sealed record Stored(string VaultId, byte[] Challenge, byte[] Wrapped);
}
