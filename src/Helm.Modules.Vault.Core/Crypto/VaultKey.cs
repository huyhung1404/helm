using System.Security.Cryptography;

namespace Helm.Modules.Vault.Crypto;

/// <summary>
/// The unlocked vault key. Held in a pinned array (the garbage collector never copies it elsewhere in memory) and
/// zeroed on <see cref="Dispose"/>, which locking the vault does. Never serialized, never logged.
/// </summary>
public sealed class VaultKey : IDisposable
{
    private readonly byte[] _key = GC.AllocateUninitializedArray<byte>(VaultCrypto.KeySize, pinned: true);
    private bool _disposed;

    public VaultKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != VaultCrypto.KeySize) throw new ArgumentException("A vault key is 32 bytes.", nameof(key));
        key.CopyTo(_key);
    }

    public static VaultKey Create()
    {
        Span<byte> key = stackalloc byte[VaultCrypto.KeySize];
        RandomNumberGenerator.Fill(key);
        try
        {
            return new VaultKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <exception cref="ObjectDisposedException">The vault was locked.</exception>
    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _key;
        }
    }

    /// <summary>A named sub-key (docs/vault-format.md); the caller zeroes it.</summary>
    public byte[] Derive(string info) => VaultCrypto.Derive(Bytes, info);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }

    public override string ToString() => "VaultKey(***)";
}
