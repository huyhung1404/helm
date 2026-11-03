using Helm.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Assistant;

/// <summary>A photo of an agent job between devices: an encrypted blob, whose key lives only in the job's record.</summary>
public sealed record AiJobPhoto(BlobRef Blob, string MediaType);

/// <summary>
/// How a job's photos travel to the PC. Never inside the record: a record holds at most 1 MiB once encrypted and sent
/// as base64 (docs/sync-protocol.md, "Limits"; live messages 64 KiB), and a photo at <see cref="AssistantPhotos.MaxSide"/>
/// is a 150-700 KB JPEG that grows by about 1.8 on the way. Each one is a blob of its own (the record waits for it to
/// reach the server, <see cref="AgentJobs.PhotosOf"/>), dropped by both sides as soon as the turn is over.
/// </summary>
public interface IAgentJobPhotos
{
    /// <summary>Encrypts the photo into a new blob (it uploads with the next sync, before the record that names it).</summary>
    Task<AiJobPhoto> PutAsync(AssistantImage image, CancellationToken ct);

    /// <summary>The photo's bytes, fetched from the server; the copy fetched is not kept on this device.</summary>
    Task<AssistantImage> ReadAsync(AiJobPhoto photo, CancellationToken ct);

    /// <summary>The photo goes: here at once, and on the server (in the background; it never throws).</summary>
    void Drop(AiJobPhoto photo);
}

/// <summary><see cref="IAgentJobPhotos"/> over Helm Sync's blob store.</summary>
public sealed class BlobJobPhotos(BlobStore blobs, ILogger? logger = null) : IAgentJobPhotos
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<AiJobPhoto> PutAsync(AssistantImage image, CancellationToken ct)
    {
        using var stream = new MemoryStream(image.Data, writable: false);
        return new AiJobPhoto(await blobs.ImportAsync(stream, ct: ct).ConfigureAwait(false), image.MediaType);
    }

    public async Task<AssistantImage> ReadAsync(AiJobPhoto photo, CancellationToken ct)
    {
        try
        {
            return new AssistantImage(await blobs.ReadAllAsync(photo.Blob, ct).ConfigureAwait(false), photo.MediaType);
        }
        finally
        {
            // Read once: the chunks it fetched go with the job.
            blobs.Discard(photo.Blob.Id);
        }
    }

    public void Drop(AiJobPhoto photo) => _ = DropAsync(photo);

    private async Task DropAsync(AiJobPhoto photo)
    {
        try
        {
            await blobs.DeleteAsync(photo.Blob.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Offline, or gone already: what is left on the server is ciphertext whose key went with the record, and the
            // daily clean-up removes a blob no record names.
            _logger.LogDebug(ex, "A job's photo could not be deleted on the sync server now");
        }
    }
}
