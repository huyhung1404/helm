namespace Helm.Embeddings;

/// <summary>
/// Turns text into vectors that say what it is about, so texts can be compared by meaning (<see cref="EmbeddingVector.Cosine"/>).
/// Works only on a device that has the model; elsewhere it gives nothing and callers search by words as before.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>The model the vectors come from: vectors of different models cannot be compared.</summary>
    string ModelId { get; }

    /// <summary>The model is on this device.</summary>
    bool IsAvailable { get; }

    /// <summary>The model was downloaded or removed; may come on any thread.</summary>
    event EventHandler? AvailabilityChanged;

    /// <summary>The vector of a short text (a search), cut at what the model reads at once; null without the model.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>A vector for each part of a long text (a note), in order; empty without the model.</summary>
    Task<IReadOnlyList<float[]>> EmbedPartsAsync(string text, CancellationToken ct = default);
}
