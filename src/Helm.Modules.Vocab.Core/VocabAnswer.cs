using System.Text;

namespace Helm.Modules.Vocab;

public enum VocabAnswerVerdict
{
    Correct,
    /// <summary>One or two letters off, or the right pinyin with wrong tones.</summary>
    Close,
    Wrong,
}

public enum VocabAnswerMark
{
    /// <summary>Typed as it should be.</summary>
    Ok,
    /// <summary>Typed but wrong (or one too many).</summary>
    Wrong,
    /// <summary>Not typed: shown so the user sees what was left out.</summary>
    Missing,
}

/// <summary>A run of the typed answer with how it compares (the "Type the word" card colours them).</summary>
public sealed record VocabAnswerPiece(string Text, VocabAnswerMark Mark)
{
    public bool IsOk => Mark == VocabAnswerMark.Ok;

    public bool IsWrong => Mark == VocabAnswerMark.Wrong;

    public bool IsMissing => Mark == VocabAnswerMark.Missing;
}

public sealed record VocabAnswerResult(VocabAnswerVerdict Verdict, IReadOnlyList<VocabAnswerPiece> Pieces, string Message);

/// <summary>
/// Checks a typed answer against a word. English: case, extra spaces and hyphens do not matter; one letter off (two in
/// a long word) is close. Chinese: the characters (simplified or traditional), or the pinyin, with tone marks, tone
/// numbers (bu3jiu4) or no tones at all (then only the sounds are checked).
/// </summary>
public static class VocabAnswer
{
    private const string Marked = "āáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜ";
    private const string Bases = "aeiouv";

    /// <summary>A Chinese word's pinyin, typed with tone marks, tone numbers or no tones (practising the reading).</summary>
    public static VocabAnswerResult CheckPinyin(VocabWord word, string typed)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return new VocabAnswerResult(VocabAnswerVerdict.Wrong, [], $"Not typed. Its pinyin: {word.Phonetic}.");
        if (typed.Any(VocabText.IsHan))
            return new VocabAnswerResult(VocabAnswerVerdict.Wrong, [], $"Type the pinyin in letters (tones optional): {word.Phonetic}.");
        return Pinyin(word, typed);
    }

    public static VocabAnswerResult Check(VocabWord word, string typed)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return new VocabAnswerResult(VocabAnswerVerdict.Wrong, [], "Nothing typed.");
        if (word.Language == VocabLanguage.Chinese)
            return typed.Any(VocabText.IsHan) ? Characters(word, typed) : Pinyin(word, typed);
        return English(word.Text, typed);
    }

    private static VocabAnswerResult English(string expected, string typed)
    {
        static string Key(string s) => string.Join(' ', s.ToLowerInvariant().Replace('’', '\'').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var (want, got) = (Key(expected), Key(typed));
        if (want == got) return new VocabAnswerResult(VocabAnswerVerdict.Correct, [new VocabAnswerPiece(typed, VocabAnswerMark.Ok)], "Correct!");
        var distance = Distance(want, got);
        var close = want.Length >= 4 && distance <= (want.Length >= 8 ? 2 : 1);
        return new VocabAnswerResult(close ? VocabAnswerVerdict.Close : VocabAnswerVerdict.Wrong, Diff(want, got),
            close ? "Almost: check the spelling." : "Not quite.");
    }

    private static VocabAnswerResult Characters(VocabWord word, string typed)
    {
        var got = string.Concat(typed.Where(c => !char.IsWhiteSpace(c)));
        if (got == word.Text || word.Traditional.Length > 0 && got == word.Traditional)
            return new VocabAnswerResult(VocabAnswerVerdict.Correct, [new VocabAnswerPiece(got, VocabAnswerMark.Ok)], "Correct!");
        return new VocabAnswerResult(VocabAnswerVerdict.Wrong, Diff(word.Text, got), "Not quite.");
    }

    private static VocabAnswerResult Pinyin(VocabWord word, string typed)
    {
        var (want, wantTones) = Syllables(word.Phonetic);
        var (got, gotTones) = Syllables(typed);
        if (want != got) return new VocabAnswerResult(VocabAnswerVerdict.Wrong, Diff(want, got), "Not quite.");
        var pieces = new[] { new VocabAnswerPiece(typed, VocabAnswerMark.Ok) };
        if (gotTones.Count == 0)
            return new VocabAnswerResult(VocabAnswerVerdict.Correct, pieces, $"Right sounds (tones not checked: {word.Phonetic}).");
        return gotTones.SequenceEqual(wantTones)
            ? new VocabAnswerResult(VocabAnswerVerdict.Correct, pieces, "Correct!")
            : new VocabAnswerResult(VocabAnswerVerdict.Close, [new VocabAnswerPiece(typed, VocabAnswerMark.Wrong)], $"Right sounds, wrong tones: {word.Phonetic}.");
    }

    /// <summary>Pinyin as plain letters (ü as v, no spaces or apostrophes) and its tones 1-4 in order (neutral left out).</summary>
    internal static (string Letters, IReadOnlyList<int> Tones) Syllables(string pinyin)
    {
        var letters = new StringBuilder();
        var tones = new List<int>();
        var text = pinyin.ToLowerInvariant().Replace("u:", "v");
        foreach (var c in text)
        {
            var at = Marked.IndexOf(c);
            if (at >= 0)
            {
                letters.Append(Bases[at / 4]);
                tones.Add(at % 4 + 1);
            }
            else if (c is >= '1' and <= '4') tones.Add(c - '0');
            else if (c == 'ü') letters.Append('v');
            else if (char.IsLetter(c)) letters.Append(c);
        }
        return (letters.ToString(), tones);
    }

    /// <summary>Edit distance (insert, delete, replace one character).</summary>
    internal static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>The typed text lined up against the expected one: right letters, wrong ones, and the ones left out.</summary>
    internal static IReadOnlyList<VocabAnswerPiece> Diff(string expected, string typed)
    {
        var (n, m) = (expected.Length, typed.Length);
        var cost = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) cost[i, 0] = i;
        for (var j = 0; j <= m; j++) cost[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                cost[i, j] = Math.Min(Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1), cost[i - 1, j - 1] + (expected[i - 1] == typed[j - 1] ? 0 : 1));
        var marks = new List<(char C, VocabAnswerMark Mark)>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && cost[i, j] == cost[i - 1, j - 1] + (expected[i - 1] == typed[j - 1] ? 0 : 1))
            {
                marks.Add((typed[j - 1], expected[i - 1] == typed[j - 1] ? VocabAnswerMark.Ok : VocabAnswerMark.Wrong));
                i--;
                j--;
            }
            else if (j > 0 && cost[i, j] == cost[i, j - 1] + 1)
            {
                marks.Add((typed[j - 1], VocabAnswerMark.Wrong));
                j--;
            }
            else
            {
                marks.Add((expected[i - 1], VocabAnswerMark.Missing));
                i--;
            }
        }
        marks.Reverse();
        var pieces = new List<VocabAnswerPiece>();
        foreach (var (c, mark) in marks)
        {
            if (pieces.Count > 0 && pieces[^1].Mark == mark) pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + c };
            else pieces.Add(new VocabAnswerPiece(c.ToString(), mark));
        }
        return pieces;
    }
}
