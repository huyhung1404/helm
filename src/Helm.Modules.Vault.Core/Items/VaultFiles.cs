using Helm.Core.Sync;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.Items;

/// <summary>
/// Documents attached to vault items. A file is encrypted into a blob with its own random key; that key is stored
/// only inside the item, so the vault key protects the file. Removing an attachment keeps it in the item's history
/// (and so on the server) until the history moves past it.
/// </summary>
public sealed class VaultFiles(VaultStore store, BlobStore blobs, VaultSession session)
{
    /// <summary>
    /// Encrypts <paramref name="content"/> into a blob of its own without touching any item: the editor keeps it until
    /// Save puts it into the item (one new version), and drops it on Cancel.
    /// </summary>
    /// <exception cref="BlobTooLargeException">Larger than the sync server accepts.</exception>
    public async Task<VaultAttachment> ImportAsync(string name, string mediaType, Stream content,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        _ = session.Key; // Fail before the (possibly long) encryption when the vault is locked.
        var blob = await blobs.ImportAsync(content, progress, ct).ConfigureAwait(false);
        return new VaultAttachment(SyncIds.NewId(), name, mediaType, blob.Size, blob);
    }

    /// <summary>Encrypts <paramref name="content"/> and adds it to the item. The upload happens with the next sync.</summary>
    /// <exception cref="BlobTooLargeException">Larger than the sync server accepts.</exception>
    public async Task<VaultAttachment> AttachAsync(string uid, string name, string mediaType, Stream content,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        _ = session.Key; // Fail before the (possibly long) encryption when the vault is locked.
        var blob = await blobs.ImportAsync(content, progress, ct).ConfigureAwait(false);
        var attachment = new VaultAttachment(SyncIds.NewId(), name, mediaType, blob.Size, blob);
        var item = store.Get(uid)?.Item ?? throw new ArgumentException("No such item.", nameof(uid));
        store.Save(uid, item with { Attachments = [.. item.Attachments, attachment] });
        return attachment;
    }

    /// <summary>Detaches a file. The previous version of the item (with the file) stays in its history.</summary>
    public void Remove(string uid, string attachmentId)
    {
        var item = store.Get(uid)?.Item ?? throw new ArgumentException("No such item.", nameof(uid));
        store.Save(uid, item with { Attachments = item.Attachments.Where(a => a.Id != attachmentId).ToList() });
    }

    /// <summary>Decrypts the file into <paramref name="destination"/> (memory, or the app's private temp folder).</summary>
    public Task OpenAsync(VaultAttachment attachment, Stream destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        _ = session.Key;
        return blobs.ReadAsync(attachment.Blob, destination, progress, ct);
    }

    public Task<byte[]> ReadAllAsync(VaultAttachment attachment, CancellationToken ct = default)
    {
        _ = session.Key;
        return blobs.ReadAllAsync(attachment.Blob, ct);
    }

    /// <summary>True when the file opens without the network.</summary>
    public bool IsOnThisDevice(VaultAttachment attachment) => blobs.IsCached(attachment.Blob);

    /// <summary>True while the file exists only on this device (not uploaded yet).</summary>
    public bool IsUploading(VaultAttachment attachment) => blobs.IsPending(attachment.Blob.Id);

    public Task KeepOnThisDeviceAsync(VaultAttachment attachment, CancellationToken ct = default) => blobs.CacheAsync(attachment.Blob, ct);
}
