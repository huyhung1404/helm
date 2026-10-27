using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// Turns a phoneme string into VieNeu's token ids, as Hugging Face's tokenizers library does with the model's
/// tokenizer.json: its special tokens kept whole, NFC, the GPT-2 split pattern, byte-level symbols, then byte-pair merges
/// by rank. Only the parts that file uses are implemented (a byte-level BPE with no prefix or suffix, no dropout).
/// </summary>
public sealed class PhonemeTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly IReadOnlyList<(string Content, int Id)> _added;
    private readonly Regex _split;
    private readonly int _unknown;

    private static readonly string[] ByteSymbols = BuildByteSymbols();

    private PhonemeTokenizer(Dictionary<string, int> vocab, Dictionary<(string, string), int> ranks, IReadOnlyList<(string, int)> added,
        Regex split, int unknown)
    {
        _vocab = vocab;
        _ranks = ranks;
        _added = added;
        _split = split;
        _unknown = unknown;
    }

    public static PhonemeTokenizer Load(string tokenizerJson)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(tokenizerJson));
        var root = document.RootElement;
        var model = root.GetProperty("model");
        if (model.GetProperty("type").GetString() != "BPE") throw new InvalidDataException("VieNeu's tokenizer is expected to be BPE.");

        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject()) vocab[entry.Name] = entry.Value.GetInt32();

        var ranks = new Dictionary<(string, string), int>();
        var rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            // Newer files write a merge as a pair, older ones as "a b".
            var (left, right) = merge.ValueKind == JsonValueKind.Array
                ? (merge[0].GetString()!, merge[1].GetString()!)
                : SplitPair(merge.GetString()!);
            ranks.TryAdd((left, right), rank++);
        }

        var added = root.GetProperty("added_tokens").EnumerateArray()
            .Select(t => (t.GetProperty("content").GetString()!, t.GetProperty("id").GetInt32()))
            .OrderByDescending(t => t.Item1.Length)
            .ToList();

        var pattern = FindSplitPattern(root.GetProperty("pre_tokenizer"))
            ?? throw new InvalidDataException("VieNeu's tokenizer has no split pattern.");
        var unknownToken = model.TryGetProperty("unk_token", out var unk) ? unk.GetString() : null;
        var unknown = unknownToken is not null && vocab.TryGetValue(unknownToken, out var id) ? id : 0;
        return new PhonemeTokenizer(vocab, ranks, added, new Regex(pattern, RegexOptions.CultureInvariant), unknown);
    }

    /// <summary>The ids of <paramref name="text"/>, with no special tokens added around it.</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        var start = 0;
        while (start < text.Length)
        {
            var (at, token) = NextAdded(text, start);
            if (at > start) EncodePlain(text[start..at], ids);
            if (token is null) break;
            ids.Add(token.Value.Id);
            start = at + token.Value.Content.Length;
        }
        return ids;
    }

    private (int At, (string Content, int Id)? Token) NextAdded(string text, int start)
    {
        var best = text.Length;
        (string, int)? found = null;
        foreach (var token in _added)
        {
            var at = text.IndexOf(token.Content, start, StringComparison.Ordinal);
            if (at >= 0 && at < best)
            {
                best = at;
                found = token;
            }
        }
        return (best, found);
    }

    private void EncodePlain(string text, List<int> ids)
    {
        foreach (Match piece in _split.Matches(text.Normalize(NormalizationForm.FormC)))
        {
            var symbols = Encoding.UTF8.GetBytes(piece.Value).Select(b => ByteSymbols[b]).ToList();
            Merge(symbols);
            foreach (var symbol in symbols) ids.Add(_vocab.TryGetValue(symbol, out var id) ? id : _unknown);
        }
    }

    /// <summary>Joins neighbouring symbols, the pair with the lowest rank first, until no pair has a rank.</summary>
    private void Merge(List<string> symbols)
    {
        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestAt = -1;
            for (var i = 0; i + 1 < symbols.Count; i++)
            {
                if (_ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestAt = i;
                }
            }
            if (bestAt < 0) return;
            var (left, right) = (symbols[bestAt], symbols[bestAt + 1]);
            // Every occurrence of the pair is joined in one pass, left to right, as the library does.
            for (var i = 0; i + 1 < symbols.Count; i++)
            {
                if (symbols[i] == left && symbols[i + 1] == right)
                {
                    symbols[i] = left + right;
                    symbols.RemoveAt(i + 1);
                }
            }
        }
    }

    private static string? FindSplitPattern(JsonElement preTokenizer)
    {
        if (preTokenizer.TryGetProperty("pattern", out var pattern) && pattern.TryGetProperty("Regex", out var regex)) return regex.GetString();
        if (preTokenizer.TryGetProperty("pretokenizers", out var list))
            foreach (var inner in list.EnumerateArray())
                if (FindSplitPattern(inner) is { } found) return found;
        return null;
    }

    private static (string, string) SplitPair(string merge)
    {
        var space = merge.IndexOf(' ');
        return (merge[..space], merge[(space + 1)..]);
    }

    /// <summary>GPT-2's printable stand-in for each byte: the byte itself where it is a visible Latin-1 character.</summary>
    private static string[] BuildByteSymbols()
    {
        var symbols = new string[256];
        var next = 256;
        for (var b = 0; b < 256; b++)
        {
            var visible = b is >= '!' and <= '~' or >= 0xA1 and <= 0xAC or >= 0xAE and <= 0xFF;
            symbols[b] = ((char)(visible ? b : next++)).ToString();
        }
        return symbols;
    }
}
