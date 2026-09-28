using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Modules.Vault.Crypto;

namespace Helm.Modules.Vault.Items;

/// <summary>
/// One synced record of collection <c>vault.items</c>. The item itself is sealed in <see cref="Data"/>; the fields
/// around it stay readable while the vault is locked, so the sync engine can guard deletions (<see cref="Trashed"/>)
/// and keep blobs alive (<see cref="Blobs"/>). They are bound into the AAD, so changing them breaks the seal.
/// </summary>
/// <param name="V">Envelope format.</param>
/// <param name="Vault">The vault whose key sealed <see cref="Data"/>.</param>
/// <param name="Uid">
/// The item's id. Normally equal to the record id; a KeepBoth conflict copy is a second record with the same uid,
/// which Vault shows as a conflict on that item.
/// </param>
public sealed record VaultItemRecord(
    int V,
    string Vault,
    string Uid,
    int Epoch,
    bool Trashed,
    long? TrashedAtMs,
    IReadOnlyList<string> Blobs,
    byte[] Data)
{
    public const int CurrentFormat = 1;

    public override string ToString() => $"VaultItemRecord({Uid}, trashed: {Trashed})";
}

/// <summary>Seals and opens items: AES-256-GCM with the item key, AAD <c>helm-vault/v1|item|vault|uid|epoch|trashed|blobs</c>.</summary>
internal static class VaultItemSealer
{
    private static readonly JsonSerializerOptions Json = new(HelmJson.Options) { WriteIndented = false };

    public static VaultItemRecord Seal(VaultKey key, string vaultId, int epoch, string uid, VaultItem item, bool trashed, long? trashedAtMs)
    {
        var blobs = item.AllBlobs().Select(b => b.Id).Order(StringComparer.Ordinal).ToList();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(item, Json);
        var itemKey = key.Derive("helm-vault/v1/items");
        try
        {
            var data = VaultCrypto.Seal(itemKey, plaintext, Aad(vaultId, uid, epoch, trashed, trashedAtMs, blobs));
            return new VaultItemRecord(VaultItemRecord.CurrentFormat, vaultId, uid, epoch, trashed, trashedAtMs, blobs, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(itemKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <exception cref="VaultKeyException">Another vault's key, a newer format, or tampered data.</exception>
    public static VaultItem Open(VaultKey key, VaultItemRecord record)
    {
        if (record.V != VaultItemRecord.CurrentFormat) throw new VaultKeyException("This item was saved by a newer version of Helm.");
        var itemKey = key.Derive("helm-vault/v1/items");
        byte[]? plaintext = null;
        try
        {
            var blobs = record.Blobs.Order(StringComparer.Ordinal).ToList();
            plaintext = VaultCrypto.Open(itemKey, record.Data, Aad(record.Vault, record.Uid, record.Epoch, record.Trashed, record.TrashedAtMs, blobs));
            var item = JsonSerializer.Deserialize<VaultItem>(plaintext, Json) ?? throw new VaultKeyException("This item is empty.");
            // The readable list must match the sealed item, or blob clean-up could delete a file the item needs.
            if (!item.AllBlobs().Select(b => b.Id).Order(StringComparer.Ordinal).SequenceEqual(blobs))
                throw new VaultKeyException("This item's attachment list does not match its content.");
            return item;
        }
        catch (CryptographicException)
        {
            throw new VaultKeyException("This item cannot be decrypted with this vault's key.");
        }
        catch (JsonException)
        {
            throw new VaultKeyException("This item is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(itemKey);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string Aad(string vaultId, string uid, int epoch, bool trashed, long? trashedAtMs, IReadOnlyList<string> blobs)
    {
        var blobHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', blobs))));
        return $"helm-vault/v1|item|{vaultId}|{uid}|{epoch}|{(trashed ? 1 : 0)}|{trashedAtMs ?? 0}|{blobHash}";
    }
}
