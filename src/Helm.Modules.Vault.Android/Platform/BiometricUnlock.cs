using System.Security.Cryptography;
using System.Text.Json;
using Android.OS;
using Android.Security.Keystore;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using Helm.Core.Platform;
using Helm.Core.Settings;
using Helm.Modules.Vault.Session;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using CipherMode = Javax.Crypto.CipherMode;

namespace Helm.Modules.Vault.Platform;

/// <summary>
/// Quick unlock with a fingerprint or the face: an AES-256-GCM key in the Android Keystore that works only right after
/// a strong biometric check (BiometricPrompt with a CryptoObject), and that Android deletes when a new fingerprint or
/// face is enrolled. It wraps the vault key in settings/vault/biometric.bin. The key never leaves the secure hardware.
/// </summary>
internal sealed class BiometricUnlock(HelmPaths paths, ILogger<BiometricUnlock> logger) : IVaultDeviceUnlock
{
    private const string Alias = "helm.vault.biometric.v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private string FilePath => Path.Combine(paths.ModuleDataDirectory("vault"), "biometric.bin");

    public string Name => "Fingerprint or face";

    public Task<bool> IsAvailableAsync() =>
        Task.FromResult(BiometricManager.From(AndroidApp.Context).CanAuthenticate(BiometricManager.Authenticators.BiometricStrong) == BiometricManager.BiometricSuccess);

    public bool IsEnrolled(string vaultId) => Load() is { } stored && stored.VaultId == vaultId && KeyExists();

    public async Task<bool> EnrollAsync(string vaultId, ReadOnlyMemory<byte> vaultKey, CancellationToken ct)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, CreateKey());
        var authenticated = await PromptAsync(cipher, "Turn on quick unlock", "Confirm it is you to unlock the vault with your fingerprint or face.").ConfigureAwait(true);
        if (authenticated is null) return false;
        var wrapped = authenticated.DoFinal(vaultKey.ToArray())!;
        var stored = new Stored(vaultId, authenticated.GetIV()!, wrapped);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllBytesAsync(FilePath, JsonSerializer.SerializeToUtf8Bytes(stored), ct).ConfigureAwait(true);
        logger.LogInformation("Biometric quick unlock turned on");
        return true;
    }

    public async Task<byte[]?> UnlockAsync(string vaultId, CancellationToken ct)
    {
        var stored = Load();
        if (stored is null || stored.VaultId != vaultId || GetKey() is not { } key) throw new VaultDeviceUnlockException("Quick unlock is not set up for this vault.");
        var cipher = Cipher.GetInstance(Transformation)!;
        try
        {
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, stored.Iv));
        }
        catch (KeyPermanentlyInvalidatedException ex)
        {
            throw new VaultDeviceUnlockException("A fingerprint or face was added or removed on this phone, so quick unlock was turned off. Unlock with the password.", ex);
        }
        var authenticated = await PromptAsync(cipher, "Unlock Vault", "Use your fingerprint or face.").ConfigureAwait(true);
        if (authenticated is null) return null;
        try
        {
            return authenticated.DoFinal(stored.Wrapped);
        }
        catch (Java.Lang.Exception ex) when (ex is AEADBadTagException or BadPaddingException)
        {
            throw new VaultDeviceUnlockException("The quick unlock key does not open this vault any more. Unlock with the password.", new CryptographicException(ex.Message));
        }
    }

    public void Remove()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            var store = KeyStore.GetInstance("AndroidKeyStore")!;
            store.Load(null);
            if (store.ContainsAlias(Alias)) store.DeleteEntry(Alias);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove the biometric key");
        }
    }

    /// <returns>The cipher unlocked by the user's biometric, or null when they cancelled.</returns>
    private static Task<Cipher?> PromptAsync(Cipher cipher, string title, string subtitle)
    {
        var activity = ActivityHost.Current ?? throw new VaultDeviceUnlockException("Open Helm to use quick unlock.");
        var done = new TaskCompletionSource<Cipher?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = new BiometricPrompt.PromptInfo.Builder()
            .SetTitle(title)
            .SetSubtitle(subtitle)
            .SetNegativeButtonText("Use password")
            .SetAllowedAuthenticators(BiometricManager.Authenticators.BiometricStrong)
            .Build();
        var prompt = new BiometricPrompt(activity, ContextCompat.GetMainExecutor(activity)!, new Callback(done));
        prompt.Authenticate(info, new BiometricPrompt.CryptoObject(cipher));
        return done.Task;
    }

    private static IKey CreateKey()
    {
        var builder = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .SetUserAuthenticationRequired(true)
            .SetInvalidatedByBiometricEnrollment(true);
        // Every use needs a fresh biometric check (no time window).
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) builder.SetUserAuthenticationParameters(0, (int)KeyPropertiesAuthType.BiometricStrong);
#pragma warning disable CA1422, CS0618 // The pre-Android 11 way to say the same: authenticate for every use.
        else builder.SetUserAuthenticationValidityDurationSeconds(-1);
#pragma warning restore CA1422, CS0618
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(builder.Build());
        return generator.GenerateKey()!;
    }

    private static IKey? GetKey()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        return store.GetKey(Alias, null);
    }

    private static bool KeyExists()
    {
        try
        {
            return GetKey() is not null;
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }

    private Stored? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(FilePath)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "The biometric quick unlock file is unreadable");
            return null;
        }
    }

    private sealed record Stored(string VaultId, byte[] Iv, byte[] Wrapped);

    private sealed class Callback(TaskCompletionSource<Cipher?> done) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) =>
            done.TrySetResult(result.CryptoObject?.Cipher);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) => done.TrySetResult(null);

        // A finger that does not match: the prompt stays open and says so; nothing to do here.
        public override void OnAuthenticationFailed() { }
    }
}
