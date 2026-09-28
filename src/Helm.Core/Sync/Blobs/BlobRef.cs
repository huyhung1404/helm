using System.Text.Json.Serialization;

namespace Helm.Core.Sync;

/// <summary>
/// A file stored through the blob store: its id on the server, sizes, and the random key its chunks are encrypted
/// with. The key is the whole secret of the file, so a <see cref="BlobRef"/> is only ever stored inside an encrypted
/// record (e.g. a vault item). See docs/sync-protocol.md, "Blobs".
/// </summary>
/// <param name="Size">Plaintext length in bytes.</param>
/// <param name="ChunkSize">Plaintext bytes per chunk (the last chunk is padded to it).</param>
public sealed record BlobRef(
    string Id,
    long Size,
    int ChunkSize,
    int ChunkCount,
    [property: JsonPropertyName("key")] byte[] Key)
{
    public override string ToString() => $"BlobRef({Id}, {Size} bytes, key ***)";
}
