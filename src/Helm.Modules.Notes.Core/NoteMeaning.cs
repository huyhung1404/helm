using Helm.Core.Sync;
using Helm.Embeddings;

namespace Helm.Modules.Notes;

/// <summary>
/// How close each note is in meaning to a search, from the notes' vectors (<see cref="NoteVectors"/>, made by
/// <see cref="NoteIndexer"/> on whichever device has the model) and the search's own vector, made here. Without the model
/// on this device there is nothing (null) and the search goes by words as before. Callable from any thread.
/// </summary>
public sealed class NoteMeaning
{
    private readonly ISyncedCollection<NoteVectors> _vectors;
    private readonly ITextEmbedder _embedder;
    private readonly object _gate = new();

    // The notes' vectors read once, until the collection changes.
    private Dictionary<string, float[][]>? _decoded;

    public NoteMeaning(ISyncedCollection<NoteVectors> vectors, ITextEmbedder embedder)
    {
        _vectors = vectors;
        _embedder = embedder;
        vectors.Changed += (_, _) => { lock (_gate) _decoded = null; };
    }

    /// <summary>The model is on this device: searches can go by meaning.</summary>
    public bool IsAvailable => _embedder.IsAvailable;

    /// <summary>The model was downloaded or removed.</summary>
    public event EventHandler? AvailabilityChanged
    {
        add => _embedder.AvailabilityChanged += value;
        remove => _embedder.AvailabilityChanged -= value;
    }

    /// <summary>
    /// For each note that has vectors, the cosine of its closest part to <paramref name="text"/> (about 0.2 for
    /// unrelated notes, 0.5 and more for notes about the same thing); null without the model.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>?> SimilaritiesAsync(string text, CancellationToken ct = default)
    {
        if (text.Trim().Length == 0 || !_embedder.IsAvailable) return null;
        if (await _embedder.EmbedAsync(text.Trim(), ct).ConfigureAwait(false) is not { } query) return null;
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, parts) in Decoded())
        {
            var best = 0f;
            foreach (var part in parts) best = Math.Max(best, EmbeddingVector.Cosine(query, part));
            scores[id] = best;
        }
        return scores;
    }

    private Dictionary<string, float[][]> Decoded()
    {
        lock (_gate)
        {
            if (_decoded is { } decoded) return decoded;
            decoded = new Dictionary<string, float[][]>(StringComparer.Ordinal);
            foreach (var record in _vectors.All())
            {
                if (record.Value.Model != _embedder.ModelId) continue;
                var parts = record.Value.Parts.Select(EmbeddingVector.Decode).OfType<float[]>().ToArray();
                if (parts.Length > 0) decoded[record.Id] = parts;
            }
            return _decoded = decoded;
        }
    }
}
