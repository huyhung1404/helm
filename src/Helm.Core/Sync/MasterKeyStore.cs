using System.Security.Cryptography;
using System.Text;

namespace Helm.Core.Sync;

/// <summary>Where this device keeps the sync master key. Null means sync has not been set up here.</summary>
public interface IMasterKeyStore
{
    byte[]? Load();

    void Save(ReadOnlySpan<byte> masterKey);

    void Clear();
}

/// <summary>
/// Keeps the master key in a file protected with DPAPI for the current Windows user, so another account on the
/// machine (or a copy of the file on another machine) cannot use it.
/// </summary>
public sealed class DpapiMasterKeyStore(string filePath) : IMasterKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Helm.Sync.MasterKey.v1");

    public string FilePath { get; } = filePath;

    public byte[]? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return key.Length == SyncKeyring.KeySize ? key : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != SyncKeyring.KeySize) throw new ArgumentException("Invalid master key length.", nameof(masterKey));
        var protectedKey = ProtectedData.Protect(masterKey.ToArray(), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, protectedKey);
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

/// <summary>Tests and tooling: keeps the key in memory only.</summary>
public sealed class InMemoryMasterKeyStore(byte[]? key = null) : IMasterKeyStore
{
    private byte[]? _key = key?.ToArray();

    public byte[]? Load() => _key?.ToArray();

    public void Save(ReadOnlySpan<byte> masterKey) => _key = masterKey.ToArray();

    public void Clear() => _key = null;
}
