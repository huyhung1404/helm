using Helm.Core.Downloads;

namespace Helm.Embeddings;

/// <summary>
/// Where the sentence model lives on this device and what it is made of: paraphrase-multilingual-MiniLM-L12-v2
/// (sentence-transformers, Apache-2.0; 50+ languages, Vietnamese and English among them) in Xenova's int8 ONNX export,
/// with its tokenizer, pinned to the revision Helm was tested with. About 130 MB, downloaded once when the user asks.
/// </summary>
public sealed class SentenceModelFiles(string folder, IReadOnlyList<PinnedFile>? files = null) : PinnedFiles(folder, files ?? All)
{
    /// <summary>Names the model in what it makes: vectors of another model (or another export) cannot be compared.</summary>
    public const string ModelId = "paraphrase-multilingual-minilm-l12-v2/q8";

    private const string Repository = "https://huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2/resolve/2c4055b12046f11709e9df2c122e59ffbdc2f900/";

    public static IReadOnlyList<PinnedFile> All { get; } =
    [
        new("tokenizer.json", Repository + "tokenizer.json", 17082913, "b60b6b43406a48bf3638526314f3d232d97058bc93472ff2de930d43686fa441"),
        new("model_quantized.onnx", Repository + "onnx/model_quantized.onnx", 118308126, "66fc00f5f29afcaff34092e1bdd20008ca3918265a82fb9695a551e510cc4ebc"),
    ];

    public string TokenizerFile => System.IO.Path.Combine(Folder, "tokenizer.json");

    public string ModelFile => System.IO.Path.Combine(Folder, "model_quantized.onnx");
}
