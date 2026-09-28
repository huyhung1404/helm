using System.Security.Cryptography;

namespace Helm.Core.Sync;

/// <summary>
/// Where this device keeps the account's keys: a serialized <see cref="SyncKeySet"/> (or, from Helm 0.5.0, one bare
/// 32-byte key). Null means sync is not unlocked here.
/// </summary>
public interface IMasterKeyStore
{
    byte[]? Load();

    void Save(ReadOnlySpan<byte> masterKey);

    void Clear();
}

/// <summary>
/// Keeps the master key in a file protected by <see cref="ISecretProtector"/> (DPAPI for the current Windows user,
/// the Android Keystore on a phone), so another account (or a copy of the file on another device) cannot use it.
/// </summary>
public sealed class ProtectedMasterKeyStore(string filePath, ISecretProtector protector) : IMasterKeyStore
{
    private const string Purpose = "Helm.Sync.MasterKey.v1";

    public string FilePath { get; } = filePath;

    public byte[]? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var key = protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
            return key.Length > 0 ? key : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.IsEmpty) throw new ArgumentException("Empty key data.", nameof(masterKey));
        var protectedKey = protector.Protect(masterKey.ToArray(), Purpose);
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

/// <summary>
/// The device-local key that encrypts the replica at rest (<see cref="SyncDatabase"/>). It never leaves this
/// device and is unrelated to the account key; losing it only means the replica is rebuilt from the server.
/// </summary>
public sealed class ProtectedLocalKeyStore(string filePath, ISecretProtector protector)
{
    private const string Purpose = "Helm.Sync.LocalKey.v1";

    public string FilePath { get; } = filePath;

    public byte[] GetOrCreate()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var key = protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
                if (key.Length == SyncKeyring.KeySize) return key;
            }
            catch (CryptographicException)
            {
                // Unreadable for this user (profile reset, file copied from elsewhere): replace it below.
            }
        }
        var created = RandomNumberGenerator.GetBytes(SyncKeyring.KeySize);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(created, Purpose));
        File.Move(temp, FilePath, overwrite: true);
        return created;
    }
}
