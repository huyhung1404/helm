using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;

namespace Helm.Modules.Vault.Backup;

/// <summary>One record of a snapshot, exactly as it was synced (still sealed with the vault's item key).</summary>
public sealed record SnapshotRecord(string Id, VaultItemRecord Record);

/// <summary>A file a snapshot needs: its chunks are in the repository under <c>blobs/&lt;id&gt;/</c>.</summary>
public sealed record SnapshotBlob(string Id, int ChunkCount);

/// <summary>The decrypted content of a snapshot file.</summary>
public sealed record VaultSnapshot(
    int V,
    string VaultId,
    long CreatedAtMs,
    string Device,
    IReadOnlyList<SnapshotRecord> Records,
    IReadOnlyList<SnapshotBlob> Blobs,
    string Digest)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// The on-disk layout of a backup repository (docs/vault-format.md):
/// <code>
/// HelmVault-&lt;vaultId&gt;/
///   README.txt                  what this is and how to restore it
///   keyring.json                the vault keyring (the vault key wrapped by password and recovery key)
///   snapshots/&lt;utc&gt;.hvs        AES-256-GCM(HKDF(vault key, "helm-vault/v1/backup"), snapshot JSON)
///   blobs/&lt;blobId&gt;/&lt;n&gt;.chunk   the files' chunks, byte for byte as synced
/// </code>
/// Opening anything needs the vault password or the recovery key; nothing else, not Helm's server, not this device.
/// </summary>
public sealed class VaultBackupRepository
{
    public const string Prefix = "HelmVault-";
    private const string SnapshotExtension = ".hvs";
    private static readonly JsonSerializerOptions Json = new(HelmJson.Options) { WriteIndented = false };

    private VaultBackupRepository(IBackupTarget target, string directory, VaultKeyringData keyring, IReadOnlyList<string> snapshots)
    {
        Target = target;
        Directory = directory;
        Keyring = keyring;
        Snapshots = snapshots;
    }

    public IBackupTarget Target { get; }

    /// <summary>The repository folder inside the target, e.g. "HelmVault-01J…".</summary>
    public string Directory { get; }

    public VaultKeyringData Keyring { get; }

    public string VaultId => Keyring.VaultId;

    /// <summary>Snapshot names, newest first.</summary>
    public IReadOnlyList<string> Snapshots { get; }

    public static string DirectoryFor(string vaultId) => Prefix + vaultId;

    public static string SnapshotName(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + SnapshotExtension;

    public static DateTimeOffset? SnapshotTime(string name) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(name), "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero)
            : null;

    /// <summary>Every vault repository in the target (normally one).</summary>
    public static async Task<IReadOnlyList<VaultBackupRepository>> FindAsync(IBackupTarget target, CancellationToken ct = default)
    {
        var found = new List<VaultBackupRepository>();
        foreach (var directory in await target.ListDirectoriesAsync("", ct).ConfigureAwait(false))
        {
            if (!directory.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (await OpenAsync(target, directory, ct).ConfigureAwait(false) is { } repository) found.Add(repository);
        }
        return found;
    }

    /// <returns>Null when the folder holds no readable keyring.</returns>
    public static async Task<VaultBackupRepository?> OpenAsync(IBackupTarget target, string directory, CancellationToken ct = default)
    {
        var data = await target.ReadAsync($"{directory}/keyring.json", ct).ConfigureAwait(false);
        if (data is null) return null;
        VaultKeyringData? keyring;
        try
        {
            keyring = JsonSerializer.Deserialize<VaultKeyringData>(data, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (keyring is null) return null;
        var snapshots = (await target.ListFilesAsync($"{directory}/snapshots", ct).ConfigureAwait(false))
            .Where(n => n.EndsWith(SnapshotExtension, StringComparison.Ordinal) && SnapshotTime(n) is not null)
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .ToList();
        return new VaultBackupRepository(target, directory, keyring, snapshots);
    }

    /// <exception cref="VaultKeyException">Another vault's key, a damaged or tampered snapshot, or a newer format.</exception>
    public async Task<VaultSnapshot> ReadSnapshotAsync(string name, VaultKey key, CancellationToken ct = default)
    {
        var data = await Target.ReadAsync($"{Directory}/snapshots/{name}", ct).ConfigureAwait(false)
            ?? throw new VaultKeyException($"The snapshot {name} is missing.");
        return Open(data, name, key, VaultId);
    }

    /// <summary>One chunk of a file of the backup, as stored.</summary>
    public async Task<byte[]> ReadChunkAsync(string blobId, int index, CancellationToken ct = default) =>
        await Target.ReadAsync(ChunkPath(Directory, blobId, index), ct).ConfigureAwait(false)
            ?? throw new BlobUnavailableException($"A chunk of a file is missing from the backup ({blobId}/{index}).");

    internal static string ChunkPath(string directory, string blobId, int index) => $"{directory}/blobs/{blobId}/{index}.chunk";

    internal static byte[] Seal(VaultSnapshot snapshot, string name, VaultKey key)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        var backupKey = key.Derive("helm-vault/v1/backup");
        try
        {
            return VaultCrypto.Seal(backupKey, plaintext, Aad(snapshot.VaultId, name));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(backupKey);
        }
    }

    internal static VaultSnapshot Open(byte[] data, string name, VaultKey key, string vaultId)
    {
        var backupKey = key.Derive("helm-vault/v1/backup");
        byte[]? plaintext = null;
        try
        {
            plaintext = VaultCrypto.Open(backupKey, data, Aad(vaultId, name));
            var snapshot = JsonSerializer.Deserialize<VaultSnapshot>(plaintext, Json) ?? throw new VaultKeyException("The snapshot is empty.");
            if (snapshot.V != VaultSnapshot.CurrentFormat) throw new VaultKeyException("This backup was made by a newer version of Helm.");
            if (snapshot.VaultId != vaultId || snapshot.Digest != DigestOf(snapshot.Records))
                throw new VaultKeyException("The snapshot does not match its own content.");
            return snapshot;
        }
        catch (CryptographicException)
        {
            throw new VaultKeyException("The snapshot cannot be decrypted with this vault's key (damaged, or renamed).");
        }
        catch (JsonException)
        {
            throw new VaultKeyException("The snapshot is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(backupKey);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>SHA-256 over the records' ids and sealed data in id order: what "the same backup" means.</summary>
    internal static string DigestOf(IEnumerable<SnapshotRecord> records)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var record in records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(record.Id + "\n"));
            hash.AppendData(record.Record.Data);
            hash.AppendData(Encoding.UTF8.GetBytes(record.Record.Trashed ? "\n1\n" : "\n0\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // The file name is bound in: an old snapshot cannot be passed off as a newer one by renaming it.
    private static string Aad(string vaultId, string name) => $"helm-vault/v1/snapshot|{vaultId}|{name}";

    internal static byte[] KeyringJson(VaultKeyringData keyring) => JsonSerializer.SerializeToUtf8Bytes(keyring, new JsonSerializerOptions(Json) { WriteIndented = true });

    internal const string Readme = """
        This folder is an encrypted backup of a Helm Vault (https://github.com/huyhung1404/helm).

        Nothing in it can be read without the vault password or the recovery key from the Emergency Kit.
        - keyring.json: the vault key, wrapped by the password and by the recovery key
        - snapshots/: the vault's items at each backup (newest file = latest)
        - blobs/: the attached documents, in encrypted chunks

        To restore: in Helm, open Vault and choose "Restore from backup", then pick the folder that contains this one.
        Without Helm: run helm-vault-restore (attached to every Helm release) on this folder; it writes the items and
        documents, decrypted, into a folder you choose. The format is described in docs/vault-format.md.

        Do not edit or rename files here. Deleting old snapshots is safe; Helm prunes them itself.
        """;
}
