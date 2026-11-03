using System.Security.Cryptography;
using System.Text;
using Helm.Core.Sync;

namespace Helm.Modules.Notes;

/// <summary>
/// What a note is about, as the sentence model sees it (synced record in <c>notes.vectors</c>, keyed by the note's id):
/// a vector for each part of its text (<see cref="Helm.Embeddings.EmbeddingVector"/>), and the hash of the text they
/// were made from, so an edit is noticed. Made on a device that has the model; every device can search with them.
/// End-to-end encrypted like the notes: the vectors say something about the text.
/// </summary>
public sealed record NoteVectors
{
    public const string Collection = "notes.vectors";

    /// <summary>Vectors are refreshed by whoever has the model: the last one written is right.</summary>
    public static SyncedCollectionOptions<NoteVectors> Options { get; } = new() { Name = Collection };

    /// <summary>The model that made them (<see cref="Helm.Embeddings.ITextEmbedder.ModelId"/>).</summary>
    public string Model { get; init; } = "";

    /// <summary><see cref="HashOf"/> the note when they were made.</summary>
    public string Hash { get; init; } = "";

    public IReadOnlyList<string> Parts { get; init; } = [];

    /// <summary>The text that is embedded: the title (when it has one) and the text.</summary>
    public static string TextOf(NoteItem note) => note.Title.Trim().Length == 0 ? note.Body : note.Title.Trim() + "\n" + note.Body;

    public static string HashOf(NoteItem note) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TextOf(note))), 0, 16).ToLowerInvariant();
}
