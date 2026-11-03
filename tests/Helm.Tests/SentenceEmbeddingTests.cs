using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Helm.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The sentence model's tokenizer against Hugging Face tokenizers (the ids in Fixtures/sentence-model/reference.json,
/// from a cut-down copy of its tokenizer.json with every piece the samples can use), the small vectors kept for sync,
/// cutting a long note in parts, and (where the model is on this PC) the same vectors as onnxruntime in Python.
/// </summary>
public sealed class SentenceEmbeddingTests(ITestOutputHelper output)
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sentence-model");

    private static readonly Lazy<UnigramTokenizer> CutTokenizer = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(Fixtures, "tokenizer-cut.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        gzip.CopyTo(bytes);
        return UnigramTokenizer.Parse(bytes.ToArray());
    });

    private static JsonElement Reference => JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "reference.json"))).RootElement;

    /// <summary>The model's files: HELM_SENTENCE_MODEL, else where they were downloaded by hand. CI has neither.</summary>
    internal static string? ModelFolder()
    {
        foreach (var folder in new[] { Environment.GetEnvironmentVariable("HELM_SENTENCE_MODEL"), @"C:\tmp\sentence-model" })
            if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "model_quantized.onnx")) && File.Exists(Path.Combine(folder, "tokenizer.json")))
                return folder;
        return null;
    }

    public static TheoryData<int> Samples() => [.. Enumerable.Range(0, Reference.GetProperty("tokens").GetArrayLength())];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Vietnamese_English_and_odd_text_get_the_reference_ids(int sample)
    {
        var (text, ids) = TokenSample(sample);

        Assert.Equal(ids, CutTokenizer.Value.Encode(text));
    }

    [Fact]
    public void A_long_text_is_cut_to_what_the_model_reads_and_still_ends_with_its_end_token()
    {
        var tokenizer = CutTokenizer.Value;
        var ids = tokenizer.Encode(string.Join(' ', Enumerable.Repeat("Ghi chú về lỗi server hôm trước", 40)), SentenceModel.MaxTokens);

        Assert.Equal(SentenceModel.MaxTokens, ids.Count);
        Assert.Equal(tokenizer.StartId, ids[0]);
        Assert.Equal(tokenizer.EndId, ids[^1]);
    }

    [Fact]
    public void A_long_note_is_cut_in_parts_of_whole_words_each_one_the_model_can_read()
    {
        var tokenizer = CutTokenizer.Value;
        var sentence = TokenSample(1).Text;
        var words = tokenizer.EncodeWords(sentence).ToList();
        var text = string.Join('\n', Enumerable.Repeat(sentence, 30));

        var parts = SentenceEmbedder.Parts(tokenizer, text);

        Assert.InRange(parts.Count, 2, SentenceEmbedder.MaxParts);
        Assert.All(parts, p =>
        {
            Assert.InRange(p.Count, 3, SentenceModel.MaxTokens);
            Assert.Equal(tokenizer.StartId, p[0]);
            Assert.Equal(tokenizer.EndId, p[^1]);
        });
        // Nothing is lost or doubled between the parts.
        Assert.Equal(words.Sum(w => w.Length) * 30, parts.Sum(p => p.Count - 2));
        // A short note is one part, the same ids as a search for the same text.
        Assert.Equal([tokenizer.Encode(sentence)], SentenceEmbedder.Parts(tokenizer, sentence));
        // A note far too long has its first parts read only.
        Assert.Equal(SentenceEmbedder.MaxParts, SentenceEmbedder.Parts(tokenizer, string.Join('\n', Enumerable.Repeat(sentence, 400))).Count);
    }

    [Fact]
    public void A_vector_kept_in_bytes_points_the_same_way()
    {
        var random = new Random(7);
        var vector = Enumerable.Range(0, SentenceModel.Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1) * 0.1f).ToArray();
        EmbeddingVector.Normalize(vector);

        var encoded = EmbeddingVector.Encode(vector);
        var decoded = EmbeddingVector.Decode(encoded)!;

        Assert.Equal(512, encoded.Length);
        Assert.True(EmbeddingVector.Cosine(vector, decoded) > 0.999f);
        Assert.Null(EmbeddingVector.Decode("not base64!"));
        Assert.Null(EmbeddingVector.Decode(EmbeddingVector.Encode(new float[8])));
        Assert.Equal(0, EmbeddingVector.Cosine(vector, decoded[..10]));
    }

    [Fact]
    public void The_real_model_is_pinned_to_a_tested_revision_and_hashes()
    {
        Assert.All(SentenceModelFiles.All, f =>
        {
            Assert.StartsWith("https://huggingface.co/", f.Url);
            Assert.Matches("/resolve/[0-9a-f]{40}/", f.Url);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
        });
        Assert.InRange(new SentenceModelFiles(Path.GetTempPath()).TotalSize, 120_000_000, 140_000_000);
    }

    [Fact]
    public async Task Without_the_model_nothing_is_embedded()
    {
        var folder = Path.Combine(Path.GetTempPath(), "helm-sentence-" + Guid.NewGuid().ToString("N"));
        using var embedder = new SentenceEmbedder(new SentenceModelInstaller(new SentenceModelFiles(folder), NullLogger<SentenceModelInstaller>.Instance),
            NullLogger<SentenceEmbedder>.Instance);

        Assert.False(embedder.IsAvailable);
        Assert.Null(await embedder.EmbedAsync("lỗi server"));
        Assert.Empty(await embedder.EmbedPartsAsync("lỗi server"));
    }

    [Fact]
    public void The_real_tokenizer_gives_the_reference_ids()
    {
        if (ModelFolder() is not { } folder) return;
        var watch = Stopwatch.StartNew();
        var tokenizer = UnigramTokenizer.Load(Path.Combine(folder, "tokenizer.json"));
        output.WriteLine($"tokenizer loaded in {watch.ElapsedMilliseconds} ms");

        for (var i = 0; i < Reference.GetProperty("tokens").GetArrayLength(); i++)
        {
            var (text, ids) = TokenSample(i);
            Assert.Equal(ids, tokenizer.Encode(text));
        }
    }

    [Fact]
    public async Task The_real_model_gives_the_vectors_of_the_reference_and_finds_by_meaning()
    {
        if (RealEmbedder() is not { } embedder) return;
        using var _ = embedder;

        var watch = Stopwatch.StartNew();
        await embedder.EmbedAsync("xin chào");
        output.WriteLine($"loaded in {watch.ElapsedMilliseconds} ms");
        foreach (var item in Reference.GetProperty("vectors").EnumerateArray())
        {
            var expected = item.GetProperty("vector").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var vector = (await embedder.EmbedAsync(item.GetProperty("text").GetString()!))!;
            Assert.True(EmbeddingVector.Cosine(expected, vector) > 0.999f, $"cosine {EmbeddingVector.Cosine(expected, vector)}");
        }

        string[] notes =
        [
            "VPS sập lúc 3 giờ sáng\nNginx trả 502, pm2 restart thì hết. Nguyên nhân: hết RAM do Mongo.",
            "Công thức phở bò\nXương bò hầm 6 tiếng, quế hồi gừng nướng.",
            "Danh sách mua sắm: sữa, trứng, bánh mì",
            "Lịch tập gym tuần này: thứ 2 ngực, thứ 4 lưng, thứ 6 chân",
        ];
        watch.Restart();
        var vectors = new List<float[]>();
        foreach (var note in notes) vectors.AddRange(await embedder.EmbedPartsAsync(note));
        output.WriteLine($"{notes.Length} notes in {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var query = (await embedder.EmbedAsync("Ghi chú về lỗi server hôm trước"))!;
        output.WriteLine($"a search in {watch.ElapsedMilliseconds} ms");

        var scores = vectors.Select(v => EmbeddingVector.Cosine(query, v)).ToList();
        output.WriteLine(string.Join(", ", scores.Select(s => s.ToString("0.000"))));
        Assert.Equal(0, scores.IndexOf(scores.Max()));
    }

    /// <summary>The sentence model where it is on this PC (<see cref="ModelFolder"/>), else null.</summary>
    internal static SentenceEmbedder? RealEmbedder()
    {
        if (ModelFolder() is not { } folder) return null;
        var files = new SentenceModelFiles(folder);
        // The files were put there by hand: mark them checked as the download does.
        foreach (var file in files.Files) File.WriteAllText(files.CheckedMarkOf(file), file.Sha256);
        return new SentenceEmbedder(new SentenceModelInstaller(files, NullLogger<SentenceModelInstaller>.Instance), NullLogger<SentenceEmbedder>.Instance);
    }

    private static (string Text, List<int> Ids) TokenSample(int index)
    {
        var item = Reference.GetProperty("tokens")[index];
        return (item.GetProperty("text").GetString()!, [.. item.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32())]);
    }
}
