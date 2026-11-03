using System.Text.Json;

namespace Helm.Embeddings;

/// <summary>
/// XLM-R's SentencePiece (Unigram) tokenizer as Hugging Face tokenizers runs it from a tokenizer.json: the precompiled
/// normalization, words split at white space each with "▁" in front, then for each word the pieces whose scores add up
/// best (Viterbi), unknown characters as &lt;unk&gt; (a run of them as one), between &lt;s&gt; and &lt;/s&gt;. The
/// same ids as the reference for the model Helm uses (tests compare them); special tokens written in the text are read
/// as text. Read-only once loaded: safe from several threads.
/// </summary>
public sealed class UnigramTokenizer
{
    /// <summary>The score of an unknown character is this much below the lowest piece, as in tokenizers.</summary>
    private const double UnknownPenalty = 10;

    private const char Space = '▁';

    private readonly Dictionary<string, int> _ids;
    private readonly double[] _scores;
    private readonly int _maxPiece;
    private readonly double _unknownScore;
    private readonly PrecompiledCharsmap? _charsmap;

    private UnigramTokenizer(Dictionary<string, int> ids, double[] scores, int unknownId, int startId, int endId, PrecompiledCharsmap? charsmap)
    {
        _ids = ids;
        _scores = scores;
        UnknownId = unknownId;
        StartId = startId;
        EndId = endId;
        _charsmap = charsmap;
        _maxPiece = ids.Count == 0 ? 1 : ids.Keys.Max(k => k.Length);
        // Over the whole vocabulary, as tokenizers does (the special tokens score 0).
        _unknownScore = scores.Min() - UnknownPenalty;
    }

    public int StartId { get; }

    public int EndId { get; }

    public int UnknownId { get; }

    /// <summary>Reads a tokenizer.json with a Unigram model (XLM-R's).</summary>
    public static UnigramTokenizer Load(string path) => Parse(File.ReadAllBytes(path));

    internal static UnigramTokenizer Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        byte[]? charsmap = null;
        var pieces = new List<(string Piece, double Score)>();
        int? unknown = null;
        string? type = null;
        // The property names down to where the reader is ("normalizer", "normalizers", null for an array item…).
        var path = new List<string?>();
        string? property = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    property = reader.GetString();
                    continue;
                case JsonTokenType.StartArray when path is [_, "model"] && property == "vocab":
                    ReadVocab(ref reader, pieces);
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    path.Add(property);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    path.RemoveAt(path.Count - 1);
                    break;
                case JsonTokenType.String when property == "precompiled_charsmap" && path.Count > 1 && path[1] == "normalizer":
                    charsmap = reader.GetBytesFromBase64();
                    break;
                case JsonTokenType.String when property == "type" && path is [_, "model"]:
                    type = reader.GetString();
                    break;
                case JsonTokenType.Number when property == "unk_id" && path is [_, "model"]:
                    unknown = reader.GetInt32();
                    break;
            }
            property = null;
        }
        if (type != "Unigram") throw new InvalidDataException($"The tokenizer is {type ?? "unknown"}, not Unigram.");
        if (pieces.Count == 0 || unknown is not { } unk || unk < 0 || unk >= pieces.Count) throw new InvalidDataException("The tokenizer has no vocabulary.");

        var ids = new Dictionary<string, int>(pieces.Count, StringComparer.Ordinal);
        var scores = new double[pieces.Count];
        for (var id = 0; id < pieces.Count; id++)
        {
            var (piece, score) = pieces[id];
            scores[id] = score;
            // The special tokens (<s>, <pad>…) are not pieces of text; empty stand-ins are nothing.
            if (piece.Length == 0 || IsSpecial(piece)) continue;
            ids.TryAdd(piece, id);
        }
        var start = pieces.FindIndex(p => p.Piece == "<s>");
        var end = pieces.FindIndex(p => p.Piece == "</s>");
        if (start < 0 || end < 0) throw new InvalidDataException("The tokenizer has no <s> or </s>.");
        return new UnigramTokenizer(ids, scores, unk, start, end, charsmap is null ? null : new PrecompiledCharsmap(charsmap));
    }

    private static bool IsSpecial(string piece) => piece is "<s>" or "</s>" or "<pad>" or "<unk>" or "<mask>";

    private static void ReadVocab(ref Utf8JsonReader reader, List<(string, double)> pieces)
    {
        // [["<s>", 0.0], [",", -3.46], …]
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var piece = reader.GetString() ?? "";
            reader.Read();
            var score = reader.GetDouble();
            reader.Read();
            pieces.Add((piece, score));
        }
    }

    /// <summary>The ids of <paramref name="text"/> between &lt;s&gt; and &lt;/s&gt;, at most <paramref name="max"/> in all.</summary>
    public List<int> Encode(string text, int max = int.MaxValue)
    {
        var ids = new List<int> { StartId };
        foreach (var word in Words(text))
        {
            EncodeWord(word, ids);
            if (ids.Count >= max - 1) break;
        }
        if (ids.Count > max - 1) ids.RemoveRange(max - 1, ids.Count - (max - 1));
        ids.Add(EndId);
        return ids;
    }

    /// <summary>The ids of each word of <paramref name="text"/>, without &lt;s&gt; and &lt;/s&gt; (for cutting a long text in parts).</summary>
    public IEnumerable<int[]> EncodeWords(string text)
    {
        var ids = new List<int>();
        foreach (var word in Words(text))
        {
            ids.Clear();
            EncodeWord(word, ids);
            yield return [.. ids];
        }
    }

    /// <summary>The normalized text cut at white space, each word with "▁" in front (and cut again before any "▁" in it).</summary>
    internal IEnumerable<string> Words(string text)
    {
        var normalized = _charsmap?.Normalize(text) ?? text;
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            if (i < normalized.Length && !char.IsWhiteSpace(normalized[i]))
            {
                if (start < 0) start = i;
                continue;
            }
            if (start < 0) continue;
            var word = normalized[start..i];
            start = -1;
            if (word[0] != Space) word = Space + word;
            var from = 0;
            for (var j = 1; j <= word.Length; j++)
            {
                if (j < word.Length && word[j] != Space) continue;
                yield return word[from..j];
                from = j;
            }
        }
    }

    /// <summary>The best pieces for one word (Viterbi over the pieces that fit at each place).</summary>
    private void EncodeWord(string word, List<int> ids)
    {
        var n = word.Length;
        var best = new double[n + 1];
        var from = new int[n + 1];
        var token = new int[n + 1];
        Array.Fill(best, double.NegativeInfinity);
        best[0] = 0;
        for (var i = 0; i < n; i++)
        {
            if (double.IsNegativeInfinity(best[i]) || char.IsLowSurrogate(word[i])) continue;
            var character = char.IsHighSurrogate(word[i]) && i + 1 < n && char.IsLowSurrogate(word[i + 1]) ? 2 : 1;
            var single = false;
            for (var length = 1; length <= Math.Min(_maxPiece, n - i); length++)
            {
                var end = i + length;
                if (end < n && char.IsLowSurrogate(word[end])) continue;
                if (!_ids.TryGetValue(word.Substring(i, length), out var id)) continue;
                single |= length == character;
                var score = best[i] + _scores[id];
                if (score > best[end]) (best[end], from[end], token[end]) = (score, i, id);
            }
            if (!single)
            {
                var end = i + character;
                var score = best[i] + _unknownScore;
                if (score > best[end]) (best[end], from[end], token[end]) = (score, i, UnknownId);
            }
        }

        var at = ids.Count;
        for (var end = n; end > 0; end = from[end])
        {
            // A run of unknown characters is one <unk>.
            if (token[end] == UnknownId && ids.Count > at && ids[at] == UnknownId) continue;
            ids.Insert(at, token[end]);
        }
    }
}
