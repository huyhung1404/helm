using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Helm.Embeddings;

/// <summary>
/// SentencePiece's normalization table ("precompiled_charsmap", nmt_nfkc for XLM-R's vocabulary): a double-array trie
/// of UTF-8 sequences and the text each one becomes. Applied as Hugging Face tokenizers does (spm_precompiled): one
/// grapheme at a time when it is shorter than six bytes, else one character at a time; whatever the table does not
/// know stays as it is.
/// </summary>
internal sealed class PrecompiledCharsmap
{
    private readonly uint[] _trie;
    private readonly byte[] _normalized;

    public PrecompiledCharsmap(byte[] map)
    {
        if (map.Length < 4) throw new InvalidDataException("The tokenizer's character map is empty.");
        var trieSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(map);
        if (trieSize % 4 != 0 || 4 + trieSize > map.Length) throw new InvalidDataException("The tokenizer's character map is damaged.");
        _trie = new uint[trieSize / 4];
        for (var i = 0; i < _trie.Length; i++) _trie[i] = BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan(4 + i * 4));
        _normalized = map[(4 + trieSize)..];
    }

    public string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var buffer = new byte[64];
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var grapheme = elements.GetTextElement();
            if (Encoding.UTF8.GetByteCount(grapheme) < 6 && Transform(grapheme, ref buffer) is { } whole)
            {
                builder.Append(whole);
                continue;
            }
            for (var i = 0; i < grapheme.Length; i++)
            {
                var length = char.IsHighSurrogate(grapheme[i]) && i + 1 < grapheme.Length ? 2 : 1;
                var c = grapheme.Substring(i, length);
                builder.Append(Transform(c, ref buffer) ?? c);
                i += length - 1;
            }
        }
        return builder.ToString();
    }

    /// <summary>The table's text for the first (shortest) prefix of <paramref name="chunk"/> it holds, like spm_precompiled.</summary>
    private string? Transform(string chunk, ref byte[] buffer)
    {
        var count = Encoding.UTF8.GetByteCount(chunk);
        if (buffer.Length < count) buffer = new byte[count * 2];
        Encoding.UTF8.GetBytes(chunk, buffer);
        if (FirstPrefix(buffer.AsSpan(0, count)) is not { } start || start >= _normalized.Length) return null;
        var end = Array.IndexOf(_normalized, (byte)0, start);
        return Encoding.UTF8.GetString(_normalized, start, (end < 0 ? _normalized.Length : end) - start);
    }

    /// <summary>darts-clone's common prefix search, stopped at its first result.</summary>
    private int? FirstPrefix(ReadOnlySpan<byte> key)
    {
        var node = 0;
        var unit = _trie[node];
        node ^= Offset(unit);
        foreach (var c in key)
        {
            if (c == 0) break;
            node ^= c;
            if ((uint)node >= _trie.Length) return null;
            unit = _trie[node];
            if (Label(unit) != c) return null;
            node ^= Offset(unit);
            if (HasLeaf(unit)) return (uint)node < _trie.Length ? (int)(_trie[node] & 0x7FFFFFFF) : null;
        }
        return null;
    }

    private static bool HasLeaf(uint unit) => ((unit >> 8) & 1) == 1;

    private static uint Label(uint unit) => unit & ((1u << 31) | 0xFF);

    private static int Offset(uint unit) => (int)((unit >> 10) << (int)((unit & (1u << 9)) >> 6));
}
