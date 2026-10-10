using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Conversion;

/// <summary>Where a dictionary entry comes from, highest priority first.</summary>
public enum LayerKind
{
    /// <summary>A name the user saved for this novel.</summary>
    BookName,
    /// <summary>A name the user saved for all novels.</summary>
    UserName,
    /// <summary>A meaning the user saved (for this novel or all).</summary>
    UserPhrase,
    /// <summary>Names.txt.</summary>
    Name,
    /// <summary>VietPhrase.txt.</summary>
    Phrase,
}

/// <summary>An entry found for a piece of Chinese, for the word popup.</summary>
public sealed record LayerEntry(LayerKind Kind, string Value);

/// <summary>
/// Chinese → Vietnamese the QuickTranslator way, with a better split. Every dictionary match at every position is a
/// candidate; the split that maximizes Σ length² × weight wins (dynamic programming), so a long phrase beats the short
/// ones inside it and the user's names beat everything. As in QuickTranslator ("prioritize names"), a phrase is not
/// used when a name starts inside it, LuatNhan rules reorder the words around a name or pronoun, and characters no
/// phrase covers are read as Hán Việt. Immutable layers: build a new converter when the user's entries change.
/// </summary>
public sealed class Converter
{
    /// <summary>No key in the usual files is longer; it bounds the lengths tried at a position.</summary>
    private const int MaxKeyLength = 40;

    private readonly DictionarySet _dictionaries;
    private readonly (PhraseDictionary Dictionary, LayerKind Kind)[] _layers;
    private readonly bool _prioritizeNames;

    public Converter(DictionarySet dictionaries, PhraseDictionary? bookNames = null, PhraseDictionary? userNames = null,
        PhraseDictionary? userPhrases = null, bool prioritizeNames = true)
    {
        _dictionaries = dictionaries;
        _prioritizeNames = prioritizeNames;
        var layers = new List<(PhraseDictionary, LayerKind)>();
        if (bookNames is { Count: > 0 }) layers.Add((bookNames, LayerKind.BookName));
        if (userNames is { Count: > 0 }) layers.Add((userNames, LayerKind.UserName));
        if (userPhrases is { Count: > 0 }) layers.Add((userPhrases, LayerKind.UserPhrase));
        layers.Add((dictionaries.Names, LayerKind.Name));
        layers.Add((dictionaries.VietPhrase, LayerKind.Phrase));
        _layers = [.. layers];
    }

    public DictionarySet Dictionaries => _dictionaries;

    /// <summary>Converts one line of the novel (a paragraph or a chapter title).</summary>
    public ConvertedLine ConvertLine(string line, ConvertMode mode = ConvertMode.VietPhrase)
    {
        var source = ChineseText.NormalizeLine(line);
        if (IsSceneBreak(source)) return new ConvertedLine(source, [], isSceneBreak: true);
        return mode switch
        {
            ConvertMode.Chinese => new ConvertedLine(source, source.Select((c, i) => new Segment(i, 1, c.ToString(),
                ChineseText.IsHan(c) ? SegmentKind.HanViet : SegmentKind.Text, "")).ToList()),
            ConvertMode.HanViet => new ConvertedLine(source, LineWriter.Write(source, HanVietPieces(source))),
            _ => new ConvertedLine(source, LineWriter.Write(source, Split(source))),
        };
    }

    /// <summary>The Hán Việt reading of each character, lower case, separated by spaces.</summary>
    public string HanVietOf(string chinese) =>
        string.Join(" ", chinese.Where(c => !char.IsWhiteSpace(c)).Select(c => ReadingOf(c) ?? c.ToString()));

    /// <summary>Every entry the layers have for <paramref name="chinese"/>, highest priority first.</summary>
    public IReadOnlyList<LayerEntry> EntriesFor(string chinese)
    {
        var key = ChineseText.NormalizeKey(chinese);
        var list = new List<LayerEntry>();
        foreach (var (dictionary, kind) in _layers)
            if (dictionary.TryGet(key, out var value)) list.Add(new LayerEntry(kind, value));
        return list;
    }

    /// <summary>A line of only stars, dashes or similar marks: a scene break.</summary>
    public static bool IsSceneBreak(string source)
    {
        var trimmed = source.Trim();
        return trimmed.Length > 0 && trimmed.All(c => c is '*' or '＊' or '-' or '=' or '~' or '·' or ' ' or '—' or '#');
    }

    private string? ReadingOf(char c) =>
        _dictionaries.HanViet.TryGet(c.ToString(), out var reading) ? ChineseText.FirstMeaning(reading) : null;

    private List<LineWriter.Piece> HanVietPieces(string source)
    {
        var pieces = new List<LineWriter.Piece>(source.Length);
        for (var i = 0; i < source.Length; i++)
            pieces.Add(FallbackAt(source, i));
        return pieces;
    }

    private LineWriter.Piece FallbackAt(string source, int i)
    {
        var c = source[i];
        if (!ChineseText.IsHan(c)) return new LineWriter.Piece(i, 1, c.ToString(), SegmentKind.Text);
        return ReadingOf(c) is { } reading
            ? new LineWriter.Piece(i, 1, reading, SegmentKind.HanViet)
            : new LineWriter.Piece(i, 1, c.ToString(), SegmentKind.Unknown);
    }

    private readonly record struct Hit(int Length, LayerKind Kind, string Text);

    /// <summary>The best split of one normalized line.</summary>
    private List<LineWriter.Piece> Split(string s)
    {
        var n = s.Length;
        var hits = new List<Hit>?[n];
        var fillers = new List<LuatNhanRules.Filler>?[n];
        // The highest rank of a name of two or more characters starting at each position (0: none).
        var nameRank = new int[n];
        var pronouns = _dictionaries.Pronouns;
        for (var i = 0; i < n; i++)
        {
            var first = s[i];
            var longest = pronouns.MaxLengthFrom(first);
            foreach (var (dictionary, _) in _layers) longest = Math.Max(longest, dictionary.MaxLengthFrom(first));
            longest = Math.Min(Math.Min(longest, n - i), MaxKeyLength);
            for (var length = longest; length >= 1; length--)
            {
                var key = s.Substring(i, length);
                foreach (var (dictionary, kind) in _layers)
                {
                    if (!dictionary.TryGet(key, out var value)) continue;
                    var text = ChineseText.FirstMeaning(value);
                    (hits[i] ??= []).Add(new Hit(length, kind, text));
                    if (IsName(kind))
                    {
                        (fillers[i] ??= []).Add(new LuatNhanRules.Filler(length, text));
                        if (length >= 2) nameRank[i] = Math.Max(nameRank[i], NameRank(kind));
                    }
                    break;
                }
                if (pronouns.TryGet(key, out var pronoun))
                    (fillers[i] ??= []).Add(new LuatNhanRules.Filler(length, ChineseText.FirstMeaning(pronoun)));
            }
        }

        IReadOnlyList<LuatNhanRules.Filler> FillersAt(int p) => p < n && fillers[p] is { } f ? f : [];
        bool Blocked(int start, int length, LayerKind kind)
        {
            if (!_prioritizeNames || IsName(kind)) return false;
            var threshold = kind == LayerKind.UserPhrase ? 2 : 1;
            for (var k = start + 1; k < start + length; k++)
                if (nameRank[k] >= threshold) return true;
            return false;
        }

        // best[i]: the highest score for s[i..]; choice[i]: the piece that starts it.
        var best = new double[n + 1];
        var choice = new LineWriter.Piece[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var fallback = FallbackAt(s, i);
            var bestScore = FallbackScore(fallback.Kind) + best[i + 1];
            var bestPiece = fallback;
            void Consider(LineWriter.Piece piece, double score)
            {
                var total = score + best[i + piece.Length];
                if (total > bestScore)
                {
                    bestScore = total;
                    bestPiece = piece;
                }
            }
            if (hits[i] is { } here)
            {
                foreach (var hit in here)
                {
                    if (Blocked(i, hit.Length, hit.Kind)) continue;
                    var kind = IsName(hit.Kind) ? SegmentKind.Name : SegmentKind.Phrase;
                    Consider(new LineWriter.Piece(i, hit.Length, hit.Text, kind), (double)hit.Length * hit.Length * Weight(hit.Kind));
                }
            }
            if (_dictionaries.LuatNhan.Count > 0 && _dictionaries.LuatNhan.MatchAt(s, i, FillersAt) is { } rule && rule.Length >= 3)
                Consider(new LineWriter.Piece(i, rule.Length, rule.Text, SegmentKind.Rule), (double)rule.Length * rule.Length * 2);
            if (ChineseNumber.TryMatchHeading(s, i, out var headingLength, out var heading))
                Consider(new LineWriter.Piece(i, headingLength, heading, SegmentKind.Rule), (double)headingLength * headingLength * 100);
            best[i] = bestScore;
            choice[i] = bestPiece;
        }

        var pieces = new List<LineWriter.Piece>();
        for (var i = 0; i < n; i += choice[i].Length) pieces.Add(choice[i]);
        return pieces;
    }

    private static bool IsName(LayerKind kind) => kind is LayerKind.BookName or LayerKind.UserName or LayerKind.Name;

    private static int NameRank(LayerKind kind) => kind == LayerKind.Name ? 1 : 2;

    /// <summary>The user's names win over any phrase around them; Names.txt wins a tie with VietPhrase.</summary>
    private static double Weight(LayerKind kind) => kind switch
    {
        LayerKind.BookName => 10,
        LayerKind.UserName => 8,
        LayerKind.UserPhrase => 3,
        LayerKind.Name => 1.1,
        _ => 1,
    };

    /// <summary>A single character from VietPhrase (score 1) beats its bare Hán Việt reading.</summary>
    private static double FallbackScore(SegmentKind kind) => kind switch
    {
        SegmentKind.HanViet => 0.5,
        SegmentKind.Unknown => 0.1,
        _ => 0,
    };
}
