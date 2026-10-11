using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.NovelReader.Grammar;

/// <summary>What kind of word a Chinese word is (from a part-of-speech dictionary).</summary>
public enum WordClass : byte
{
    Other,
    Noun,
    Name,
    Pronoun,
    Verb,
    Adjective,
    Adverb,
    Number,
    Measure,
    Time,
    Place,
    Direction,
    Preposition,
    Conjunction,
    /// <summary>的, 了, 着, 地, 得…: grammar words without a meaning of their own.</summary>
    Particle,
}

/// <summary>Looks up the class of a Chinese word; null while the dictionary is not loaded (nothing is coloured then).</summary>
public interface IWordClasses
{
    bool IsReady { get; }

    /// <summary>The class of a word the converter kept as one piece (it may be several dictionary words).</summary>
    WordClass ClassOf(string chinese);
}

/// <summary>The part a word plays in its clause.</summary>
public enum GrammarRole : byte
{
    None,
    Subject,
    Verb,
    Object,
    /// <summary>An adjective or adverb outside the subject and the object: shown a little quieter.</summary>
    Modifier,
}

/// <summary>How one segment of a paragraph is shown while the mouse is over it.</summary>
/// <param name="Band">0 or 1, alternating sentence by sentence; -1 outside every sentence.</param>
/// <param name="StartsSentence">The first segment of a sentence: the space before it stays plain, so sentences show apart.</param>
/// <param name="Dialogue">Inside quotation marks: what a character says.</param>
public readonly record struct SegmentMark(int Band, bool StartsSentence, bool Dialogue, GrammarRole Role);

/// <summary>
/// Marks a converted paragraph for reading by eye: its sentences in alternating bands, what is said in quotation marks,
/// and in each clause (a sentence cut at commas, semicolons and colons) the subject, the main verb and the object.
/// Chinese is subject–verb–object, and every converted word knows the Chinese it came from, so the classes of the
/// Chinese words give the roles: the first verb of a clause is its predicate, the noun phrase right before it the
/// subject, the noun phrase right after it the object. A clause without a verb, or a noun phrase after a preposition
/// (在寺里: "in the temple"), gets no role: better nothing than a wrong colour.
/// </summary>
public static class GrammarTagger
{
    public static IReadOnlyList<SegmentMark> Mark(ConvertedLine line, IReadOnlyList<Sentence> sentences, IWordClasses? classes)
    {
        var segments = line.Segments;
        var marks = new SegmentMark[segments.Count];
        var bands = new int[segments.Count];
        var starts = new bool[segments.Count];
        Array.Fill(bands, -1);
        foreach (var sentence in sentences)
        {
            for (var i = sentence.FirstSegment; i < sentence.FirstSegment + sentence.SegmentCount && i < segments.Count; i++)
                bands[i] = sentence.Index % 2;
            if (sentence.FirstSegment < segments.Count) starts[sentence.FirstSegment] = true;
        }

        var dialogue = Dialogue(segments);
        var roles = new GrammarRole[segments.Count];
        if (classes is { IsReady: true })
        {
            var wordClasses = new WordClass?[segments.Count];
            for (var i = 0; i < segments.Count; i++)
                wordClasses[i] = ClassOf(line, segments[i], classes);
            foreach (var clause in Clauses(segments))
                MarkClause(clause, wordClasses, roles);
        }
        for (var i = 0; i < segments.Count; i++)
            marks[i] = new SegmentMark(bands[i], starts[i], dialogue[i], roles[i]);
        return marks;
    }

    private static WordClass? ClassOf(ConvertedLine line, Segment segment, IWordClasses classes)
    {
        if (!segment.IsWord) return null;
        if (segment.Kind == SegmentKind.Name) return WordClass.Name;
        if (segment.Start < 0 || segment.Start + segment.Length > line.Source.Length) return WordClass.Other;
        return classes.ClassOf(line.Source.Substring(segment.Start, segment.Length));
    }

    // ---- What is said ------------------------------------------------------------------------------------------------

    private const string Opening = "“「『‘";
    private const string Closing = "”」』’";

    /// <summary>Which segments are inside quotation marks (the marks themselves included).</summary>
    private static bool[] Dialogue(IReadOnlyList<Segment> segments)
    {
        var inside = new bool[segments.Count];
        var open = false;
        for (var i = 0; i < segments.Count; i++)
        {
            var text = segments[i].Before + segments[i].Text;
            var opensHere = false;
            var closesHere = false;
            foreach (var c in text)
            {
                if (Opening.Contains(c) || c == '"' && !open)
                {
                    open = true;
                    opensHere = true;
                }
                else if (Closing.Contains(c) || c == '"' && open)
                {
                    open = false;
                    closesHere = true;
                }
            }
            inside[i] = open || opensHere || closesHere;
        }
        return inside;
    }

    // ---- Clauses -----------------------------------------------------------------------------------------------------

    private const string ClauseEnds = ",，;；:：.。!！?？…“”「」『』\"";

    /// <summary>The words of each clause, by segment index (punctuation ends a clause and belongs to none).</summary>
    private static IEnumerable<List<int>> Clauses(IReadOnlyList<Segment> segments)
    {
        var clause = new List<int>();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!segment.IsWord)
            {
                if (segment.Text.Any(c => ClauseEnds.Contains(c)) || segment.Before.Any(c => ClauseEnds.Contains(c)))
                {
                    if (clause.Count > 0) yield return clause;
                    clause = [];
                }
                continue;
            }
            clause.Add(i);
        }
        if (clause.Count > 0) yield return clause;
    }

    private static bool IsNominal(WordClass? c) => c is WordClass.Noun or WordClass.Name or WordClass.Pronoun or WordClass.Place;

    /// <summary>Words that may stand inside a noun phrase before its head: 一位白衣的 (a, white-clothed, of) 女子.</summary>
    private static bool IsNounModifier(WordClass? c) => c is WordClass.Number or WordClass.Measure or WordClass.Adjective or WordClass.Particle;

    private static void MarkClause(List<int> clause, WordClass?[] classes, GrammarRole[] roles)
    {
        var verb = clause.FindIndex(i => classes[i] == WordClass.Verb);
        if (verb >= 0)
        {
            // The verb, with the verbs and complements right after it (看见, 走了出去).
            var verbEnd = verb;
            while (verbEnd + 1 < clause.Count && classes[clause[verbEnd + 1]] is WordClass.Verb or WordClass.Direction) verbEnd++;
            for (var k = verb; k <= verbEnd; k++) roles[clause[k]] = GrammarRole.Verb;

            // The subject: the noun phrase before the verb, past how and when (她轻轻地, 她惋惜地, 他正要, 他今日).
            var k2 = verb - 1;
            while (k2 >= 0 && classes[clause[k2]] is WordClass.Adverb or WordClass.Adjective or WordClass.Time or WordClass.Particle) k2--;
            if (k2 >= 0 && IsNominal(classes[clause[k2]]))
            {
                var head = k2;
                var start = head;
                // A pronoun or a name stands alone (在寺里他跪下: 他, not 寺里他); a noun takes what describes it.
                if (classes[clause[head]] is not (WordClass.Pronoun or WordClass.Name))
                    while (start - 1 >= 0 && (IsNominal(classes[clause[start - 1]]) || IsNounModifier(classes[clause[start - 1]]))) start--;
                // After a preposition it says where or with whom (在寺里, 对他), not who does it.
                var afterPreposition = start - 1 >= 0 && classes[clause[start - 1]] == WordClass.Preposition;
                if (!afterPreposition)
                    for (var k = start; k <= head; k++) roles[clause[k]] = GrammarRole.Subject;
            }

            // The object: the noun phrase after the verb, past particles (了) and its own modifiers.
            var o = verbEnd + 1;
            while (o < clause.Count && classes[clause[o]] == WordClass.Particle) o++;
            var objectStart = o;
            while (o < clause.Count && IsNounModifier(classes[clause[o]])) o++;
            if (o < clause.Count && IsNominal(classes[clause[o]]))
            {
                while (o + 1 < clause.Count && IsNominal(classes[clause[o + 1]])) o++;
                for (var k = objectStart; k <= o; k++) roles[clause[k]] = GrammarRole.Object;
            }
        }
        foreach (var i in clause)
            if (roles[i] == GrammarRole.None && classes[i] is WordClass.Adjective or WordClass.Adverb) roles[i] = GrammarRole.Modifier;
    }
}
