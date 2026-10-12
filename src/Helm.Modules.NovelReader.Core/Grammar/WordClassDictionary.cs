namespace Helm.Modules.NovelReader.Grammar;

/// <summary>
/// Classes of Chinese words from a part-of-speech list in jieba's format ("word tag" or "word freq tag" per line, the
/// tags of the People's Daily corpus: n noun, nr person, v verb, a adjective, d adverb, r pronoun…). A converted word
/// the list does not have (a longer VietPhrase entry) is cut into listed words, longest first, and takes the class of
/// its head: a verb phrase's verb (轻轻点头 → verb), a noun phrase's last noun (一位女子 → noun). Loaded off the UI
/// thread; until then nothing is coloured. Thread-safe once loaded.
/// </summary>
public sealed class WordClassDictionary : IWordClasses
{
    private readonly Func<Stream>? _open;
    private volatile Dictionary<string, WordClass>? _words;
    private int _longest;
    private Task? _loading;
    private readonly object _gate = new();

    /// <summary>A dictionary read from <paramref name="open"/> the first time it is needed (see <see cref="LoadAsync"/>).</summary>
    public WordClassDictionary(Func<Stream> open) => _open = open;

    /// <summary>A dictionary of the given words (tests).</summary>
    public WordClassDictionary(IEnumerable<(string Word, WordClass Class)> words)
    {
        var map = new Dictionary<string, WordClass>(StringComparer.Ordinal);
        foreach (var (word, c) in words)
        {
            map[word] = c;
            _longest = Math.Max(_longest, word.Length);
        }
        _words = map;
    }

    public bool IsReady => _words is not null;

    /// <summary>Reads the list in the background (once); <see cref="IsReady"/> turns true when it is done.</summary>
    public Task LoadAsync()
    {
        lock (_gate)
            return _loading ??= _words is not null || _open is null ? Task.CompletedTask : Task.Run(Load);
    }

    private void Load()
    {
        var map = new Dictionary<string, WordClass>(400_000, StringComparer.Ordinal);
        var longest = 0;
        using var reader = new StreamReader(_open!());
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(' ', '\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            var c = FromTag(parts[^1]);
            if (c == WordClass.Other && map.ContainsKey(parts[0])) continue;
            map[parts[0]] = c;
            longest = Math.Max(longest, parts[0].Length);
        }
        _longest = longest;
        _words = map;
    }

    public WordClass ClassOf(string chinese)
    {
        if (_words is not { } words || chinese.Length == 0) return WordClass.Other;
        if (words.TryGetValue(chinese, out var c))
            return SaysSomething(chinese) && c is not (WordClass.Noun or WordClass.Name or WordClass.Pronoun or WordClass.Verb) ? WordClass.Verb : c;
        var parts = new List<WordClass>();
        for (var i = 0; i < chinese.Length;)
        {
            var found = false;
            for (var length = Math.Min(_longest, chinese.Length - i); length >= 1; length--)
            {
                if (!words.TryGetValue(chinese.Substring(i, length), out var part)) continue;
                parts.Add(part);
                i += length;
                found = true;
                break;
            }
            if (!found)
            {
                parts.Add(WordClass.Other);
                i++;
            }
        }
        if (SaysSomething(chinese)) return WordClass.Verb;
        return Combine(parts);
    }

    /// <summary>
    /// 轻声道, 冷笑道, 低声问: how someone says it, then the saying, which the list reads as an idiom, a measure word or a
    /// noun (a real noun such as 味道 "taste" keeps its class).
    /// </summary>
    private static bool SaysSomething(string chinese) => chinese.Length > 1 && SpeechVerbs.Contains(chinese[^1]);

    /// <summary>Verbs of saying that end a manner phrase in novels.</summary>
    private const string SpeechVerbs = "道说问答喊叫笑叹";

    /// <summary>The class of words written together, from their head.</summary>
    public static WordClass Combine(IReadOnlyList<WordClass> parts)
    {
        var content = parts.Where(p => p is not (WordClass.Particle or WordClass.Other)).ToList();
        if (content.Count == 0) return parts.Count > 0 ? parts[0] : WordClass.Other;
        var last = content[^1];
        var nominal = last is WordClass.Noun or WordClass.Name or WordClass.Pronoun or WordClass.Place;
        // 一位女子, 白衣女子, 她的手: modifiers then a noun make a noun.
        if (nominal && content.Take(content.Count - 1).All(p => p is WordClass.Number or WordClass.Measure or WordClass.Adjective or WordClass.Pronoun
                or WordClass.Noun or WordClass.Name or WordClass.Place))
            return last == WordClass.Pronoun && content.Count > 1 ? WordClass.Noun : last;
        // 点头, 轻轻点头, 抬起头: a verb anywhere makes a verb phrase.
        if (content.Contains(WordClass.Verb)) return WordClass.Verb;
        return last;
    }

    /// <summary>The class of a People's Daily (jieba) tag.</summary>
    public static WordClass FromTag(string tag)
    {
        if (tag.Length == 0) return WordClass.Other;
        if (tag.StartsWith("nr", StringComparison.Ordinal)) return WordClass.Name;
        if (tag is "ns") return WordClass.Place;
        if (tag is "an" or "vn") return WordClass.Noun;
        if (tag is "ad") return WordClass.Adverb;
        return tag[0] switch
        {
            'n' => WordClass.Noun,
            'r' => WordClass.Pronoun,
            'v' => WordClass.Verb,
            'a' => WordClass.Adjective,
            'b' => WordClass.Adjective,
            'z' => WordClass.Adjective,
            'd' => WordClass.Adverb,
            'm' => WordClass.Number,
            'q' => WordClass.Measure,
            't' => WordClass.Time,
            's' => WordClass.Place,
            'f' => WordClass.Direction,
            'p' => WordClass.Preposition,
            'c' => WordClass.Conjunction,
            'u' or 'y' or 'e' or 'o' => WordClass.Particle,
            // Idioms and set phrases (气度不凡, 恼羞成怒) describe more often than they act: never taken for the verb.
            'i' or 'l' => WordClass.Adjective,
            _ => WordClass.Other,
        };
    }
}
