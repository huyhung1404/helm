using System.Security.Cryptography;
using System.Text;
using Android.Security.Keystore;
using Helm.Core.Sync;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace Helm.App.Android.Services;

/// <summary>
/// The Android counterpart of DPAPI: an AES-256-GCM key that lives in the Android Keystore (it never leaves the
/// secure hardware and is deleted with the app). The purpose string is the GCM associated data, so one protected
/// secret cannot be passed off as another. Layout: version byte (1), 12-byte IV, ciphertext + 16-byte tag.
/// </summary>
internal sealed class AndroidKeystoreProtector : ISecretProtector
{
    private const string KeyStoreName = "AndroidKeyStore";
    private const string Alias = "helm.secrets.v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private const byte FormatVersion = 1;
    private const int IvLength = 12;
    private const int TagBits = 128;

    private readonly object _gate = new();

    public byte[] Protect(byte[] data, string purpose)
    {
        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
            cipher.UpdateAAD(Encoding.UTF8.GetBytes(purpose));
            var sealedData = cipher.DoFinal(data)!;
            var iv = cipher.GetIV()!;
            if (iv.Length != IvLength) throw new CryptographicException($"Unexpected IV length {iv.Length}.");
            return [FormatVersion, .. iv, .. sealedData];
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or Java.Lang.Throwable)
        {
            throw new CryptographicException("The Android Keystore could not protect the data.", ex);
        }
    }

    public byte[] Unprotect(byte[] data, string purpose)
    {
        if (data.Length < 1 + IvLength + TagBits / 8 || data[0] != FormatVersion) throw new CryptographicException("Not data protected by Helm on this device.");
        var key = GetKey() ?? throw new CryptographicException("This device's Helm key is gone (app data was reset).");
        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(TagBits, data, 1, IvLength));
            cipher.UpdateAAD(Encoding.UTF8.GetBytes(purpose));
            return cipher.DoFinal(data, 1 + IvLength, data.Length - 1 - IvLength)!;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or Java.Lang.Throwable)
        {
            throw new CryptographicException("The data cannot be decrypted on this device.", ex);
        }
    }

    private IKey? GetKey()
    {
        var store = KeyStore.GetInstance(KeyStoreName)!;
        store.Load(null);
        return store.GetKey(Alias, null);
    }

    private IKey GetOrCreateKey()
    {
        lock (_gate)
        {
            if (GetKey() is { } existing) return existing;
            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)!;
            generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256)
                .SetRandomizedEncryptionRequired(true)
                .Build());
            return generator.GenerateKey()!;
        }
    }
}
