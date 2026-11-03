using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Helm.Embeddings;

/// <summary>
/// paraphrase-multilingual-MiniLM-L12-v2 in ONNX Runtime, on the CPU: token ids in (<see cref="UnigramTokenizer"/>),
/// one vector of length 1 per text out, the mean of the last layer over the text's tokens (as sentence-transformers
/// does for this model). Safe to use from several threads at once: ONNX Runtime sessions are.
/// </summary>
public sealed class SentenceModel : IDisposable
{
    /// <summary>The most tokens the model reads at once, &lt;s&gt; and &lt;/s&gt; included (its max_seq_length).</summary>
    public const int MaxTokens = 128;

    public const int Dimensions = 384;

    private readonly InferenceSession _session;
    private readonly bool _hasTokenTypes;

    public SentenceModel(string modelFile, int threads)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = threads,
        };
        // Threads waiting between operators sleep instead of spinning: indexing runs in the background.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _session = new InferenceSession(modelFile, options);
        var inputs = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        if (!inputs.Contains("input_ids") || !inputs.Contains("attention_mask"))
            throw new InvalidDataException("The sentence model does not take input_ids and attention_mask.");
        _hasTokenTypes = inputs.Contains("token_type_ids");
    }

    /// <summary>
    /// The vectors of several texts' ids (each at most <see cref="MaxTokens"/>), in one run. The int8 model scales a
    /// run's numbers together, so a text's vector depends a little on the texts beside it: one text per run gives the
    /// reference's vectors.
    /// </summary>
    public float[][] Embed(IReadOnlyList<IReadOnlyList<int>> texts)
    {
        if (texts.Count == 0) return [];
        var batch = texts.Count;
        var length = texts.Max(t => t.Count);
        var ids = new DenseTensor<long>([batch, length]);
        var mask = new DenseTensor<long>([batch, length]);
        for (var b = 0; b < batch; b++)
            for (var i = 0; i < texts[b].Count; i++)
            {
                ids[b, i] = texts[b][i];
                mask[b, i] = 1;
            }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_hasTokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([batch, length])));

        using var outputs = _session.Run(inputs);
        var hidden = outputs[0].AsTensor<float>();
        var width = hidden.Dimensions[2];
        var values = (hidden as DenseTensor<float>)?.Buffer.ToArray() ?? hidden.ToArray();
        var vectors = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var vector = new float[width];
            var count = texts[b].Count;
            for (var i = 0; i < count; i++)
            {
                var row = values.AsSpan((b * length + i) * width, width);
                for (var d = 0; d < width; d++) vector[d] += row[d];
            }
            for (var d = 0; d < width; d++) vector[d] /= Math.Max(1, count);
            EmbeddingVector.Normalize(vector);
            vectors[b] = vector;
        }
        return vectors;
    }

    public void Dispose() => _session.Dispose();
}
