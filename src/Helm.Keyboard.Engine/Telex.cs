using System.Text;

namespace Helm.Keyboard;

/// <summary>How Helm Keyboard writes Vietnamese; the defaults match the EVKey setup it replaces.</summary>
public sealed record TelexOptions
{
    public static TelexOptions Default { get; } = new();

    /// <summary>oà, uý, oẻ (new style) instead of òa, úy, ỏe.</summary>
    public bool ModernToneStyle { get; init; } = true;

    /// <summary>
    /// Apply a Telex key only where the result can still be a Vietnamese word, and give back the keys as typed when the
    /// word stops being one ("class", "windows" stay as they are).
    /// </summary>
    public bool SpellCheck { get; init; } = true;
}

/// <summary>
/// Telex, one word at a time: the keys typed for a word in, the Vietnamese word out ("vieetj" → "việt").
/// The tone and the marks may be typed anywhere after the vowel ("vietej", "tieengs" and "tiengse" all work): they
/// belong to the word, and where the tone sits is worked out again after every key.
/// </summary>
public static class Telex
{
    /// <summary>A key that is part of a word: Telex words are spelled with the letters a-z only.</summary>
    public static bool IsWordKey(char key) => key is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static string Transform(string keys, TelexOptions options)
    {
        var word = new Word(options);
        foreach (var key in keys) word.Type(key);
        if (options.SpellCheck && word.HasMarks && !word.IsAcceptable()) return keys;
        return word.Render(keepTone: true, keepCase: true);
    }

    /// <summary>
    /// Keys that type <paramref name="text"/> ("việt" → "vieetj"), or null when it holds something other than letters.
    /// Used to carry on with a word after Backspace; the caller checks that the keys give the text back.
    /// </summary>
    public static string? Encode(string text)
    {
        var keys = new StringBuilder(text.Length + 4);
        var tone = Tone.None;
        foreach (var c in text)
        {
            if (!VietnameseLetters.TryDecompose(c, out var letter, out var mark, out var letterTone, out var upper)) return null;
            keys.Append(upper ? char.ToUpperInvariant(letter) : letter);
            switch (mark)
            {
                case Mark.Circumflex: keys.Append(letter); break;
                case Mark.Breve or Mark.Horn: keys.Append('w'); break;
                case Mark.Stroke: keys.Append('d'); break;
            }
            if (letterTone != Tone.None) tone = letterTone;
        }
        if (tone != Tone.None) keys.Append(ToneKey(tone));
        return keys.ToString();
    }

    private static char ToneKey(Tone tone) => tone switch
    {
        Tone.Grave => 'f',
        Tone.Acute => 's',
        Tone.Hook => 'r',
        Tone.Tilde => 'x',
        _ => 'j',
    };

    private struct Letter(char key, Mark mark = Mark.None, bool fromW = false)
    {
        /// <summary>Lowercase a-z.</summary>
        public char Key = char.ToLowerInvariant(key);
        public readonly bool Upper = char.IsUpper(key);
        public Mark Mark = mark;
        /// <summary>An ư typed as a lone "w"; a second w turns it back into a "w".</summary>
        public readonly bool FromW = fromW;

        public readonly bool IsVowel => VietnameseLetters.IsVowel(Key);
    }

    private enum Outcome
    {
        NotApplicable,
        Applied,
        Undone,
    }

    private sealed class Word(TelexOptions options)
    {
        private readonly List<Letter> _letters = [];
        private Tone _tone;
        /// <summary>
        /// The key just typed, when it was taken as a Telex key. Only that same key, straight after, takes its mark off
        /// again ("ass" → "as"; but the second w of "windows" is a plain w).
        /// </summary>
        private char _lastTelexKey;

        public bool HasMarks => _tone != Tone.None || _letters.Exists(l => l.Mark != Mark.None);

        public void Type(char key)
        {
            var lower = char.ToLowerInvariant(key);
            var lettersBefore = _letters.ToArray();
            var toneBefore = _tone;
            var outcome = lower switch
            {
                's' or 'f' or 'r' or 'x' or 'j' => SetTone(ToneOf(lower), key),
                'z' => ClearTone(),
                'a' or 'e' or 'o' => AddCircumflex(key),
                'w' => AddHornOrBreve(key),
                'd' => AddStroke(key),
                _ => Outcome.NotApplicable,
            };
            if (outcome == Outcome.Applied && options.SpellCheck && !IsAcceptable())
            {
                // Not Vietnamese with this key as a Telex key: undo it and take the key as a plain letter.
                _letters.Clear();
                _letters.AddRange(lettersBefore);
                _tone = toneBefore;
                outcome = Outcome.NotApplicable;
            }
            if (outcome == Outcome.NotApplicable) _letters.Add(new Letter(key));
            _lastTelexKey = outcome == Outcome.Applied ? lower : '\0';
        }

        private bool Repeats(char key) => _lastTelexKey == char.ToLowerInvariant(key);

        public bool IsAcceptable()
        {
            if (_tone is Tone.Grave or Tone.Hook or Tone.Tilde)
            {
                // A syllable ending in c, ch, p or t only takes sắc or nặng (các, cạch; never càc).
                var (_, end) = Cluster();
                var final = string.Concat(_letters.Skip(end).Select(l => l.Key));
                if (final is "c" or "ch" or "p" or "t") return false;
            }
            return Syllables.IsPrefix(Render(keepTone: false, keepCase: false));
        }

        public string Render(bool keepTone, bool keepCase)
        {
            var tonePosition = keepTone ? TonePosition() : -1;
            var text = new StringBuilder(_letters.Count);
            for (var i = 0; i < _letters.Count; i++)
            {
                var letter = _letters[i];
                var mark = letter.Mark;
                // ươ with nothing after it is uơ (thuở, huơ): typing w on "uo" marks both, before the final is known.
                if (mark == Mark.Horn && letter.Key == 'u' && i == _letters.Count - 2 && _letters[i + 1] is { Key: 'o', Mark: Mark.Horn })
                    mark = Mark.None;
                var tone = i == tonePosition ? _tone : Tone.None;
                text.Append(VietnameseLetters.Compose(letter.Key, mark, tone, keepCase && letter.Upper));
            }
            return text.ToString();
        }

        private static Tone ToneOf(char key) => key switch
        {
            'f' => Tone.Grave,
            's' => Tone.Acute,
            'r' => Tone.Hook,
            'x' => Tone.Tilde,
            _ => Tone.Dot,
        };

        private Outcome SetTone(Tone tone, char key)
        {
            var (start, end) = Cluster();
            if (start == end) return Outcome.NotApplicable;
            if (_tone == tone)
            {
                if (!Repeats(key)) return Outcome.NotApplicable;
                // The same tone twice takes it off and types the key: "ass" → "as".
                _tone = Tone.None;
                _letters.Add(new Letter(key));
                return Outcome.Undone;
            }
            _tone = tone;
            return Outcome.Applied;
        }

        private Outcome ClearTone()
        {
            var (start, end) = Cluster();
            if (start == end || _tone == Tone.None) return Outcome.NotApplicable;
            _tone = Tone.None;
            return Outcome.Applied;
        }

        private Outcome AddCircumflex(char key)
        {
            var lower = char.ToLowerInvariant(key);
            var at = _letters.FindLastIndex(l => l.Key == lower);
            if (at < 0) return Outcome.NotApplicable;
            var letter = _letters[at];
            if (letter.Mark == Mark.Circumflex)
            {
                if (!Repeats(key)) return Outcome.NotApplicable;
                letter.Mark = Mark.None;
                _letters[at] = letter;
                _letters.Add(new Letter(key));
                return Outcome.Undone;
            }
            letter.Mark = Mark.Circumflex;
            _letters[at] = letter;
            return Outcome.Applied;
        }

        private Outcome AddHornOrBreve(char key)
        {
            var (start, end) = Cluster();
            if (start == end)
            {
                // A lone w is ư (tw → tư), but only where no vowel has been typed yet.
                if (_letters.Exists(l => l.IsVowel)) return Outcome.NotApplicable;
                _letters.Add(new Letter(char.IsUpper(key) ? 'U' : 'u', Mark.Horn, fromW: true));
                return Outcome.Applied;
            }
            var targets = HornTargets(start, end);
            if (targets.Count == 0) return Outcome.NotApplicable;
            if (targets.TrueForAll(i => _letters[i].Mark == MarkFor(_letters[i].Key)))
            {
                if (!Repeats(key)) return Outcome.NotApplicable;
                // The same w again takes the marks off and types the w: "uww" → "uw", "ww" → "w".
                var typeW = true;
                foreach (var i in targets)
                {
                    var letter = _letters[i];
                    if (letter.FromW)
                    {
                        _letters[i] = new Letter(letter.Upper ? 'W' : 'w');
                        typeW = false;
                        continue;
                    }
                    letter.Mark = Mark.None;
                    _letters[i] = letter;
                }
                if (typeW) _letters.Add(new Letter(key));
                return Outcome.Undone;
            }
            foreach (var i in targets)
            {
                var letter = _letters[i];
                letter.Mark = MarkFor(letter.Key);
                _letters[i] = letter;
            }
            return Outcome.Applied;
        }

        private static Mark MarkFor(char key) => key == 'a' ? Mark.Breve : Mark.Horn;

        /// <summary>The vowels a w marks: both of "uo" (ươ), the a of "oa" (oă), else the first u, o or a.</summary>
        private List<int> HornTargets(int start, int end)
        {
            for (var i = start; i < end - 1; i++)
            {
                if (_letters[i].Key == 'u' && _letters[i + 1].Key == 'o') return [i, i + 1];
            }
            for (var i = start; i < end - 1; i++)
            {
                if (_letters[i].Key == 'o' && _letters[i + 1].Key == 'a') return [i + 1];
            }
            foreach (var vowel in "uoa")
            {
                for (var i = start; i < end; i++)
                {
                    if (_letters[i].Key == vowel) return [i];
                }
            }
            return [];
        }

        private Outcome AddStroke(char key)
        {
            // A d anywhere in a word that starts with d makes that one đ (dd → đ, but also "dodoir" → đổi, as EVKey does).
            if (_letters.Count == 0 || _letters[0].Key != 'd') return Outcome.NotApplicable;
            var letter = _letters[0];
            if (letter.Mark == Mark.Stroke)
            {
                if (!Repeats(key)) return Outcome.NotApplicable;
                letter.Mark = Mark.None;
                _letters[0] = letter;
                _letters.Add(new Letter(key));
                return Outcome.Undone;
            }
            letter.Mark = Mark.Stroke;
            _letters[0] = letter;
            return Outcome.Applied;
        }

        /// <summary>
        /// Where the vowels of the word are: after the first consonants, with the u of "qu" and the i of "gi" counted
        /// as consonants when another vowel follows (quý, già; but gì).
        /// </summary>
        private (int Start, int End) Cluster()
        {
            var start = _letters.FindIndex(l => l.IsVowel);
            if (start < 0) return (_letters.Count, _letters.Count);
            var vowelFollows = start + 1 < _letters.Count && _letters[start + 1].IsVowel;
            if (vowelFollows && start > 0 && _letters[start].Key == 'u' && _letters[start - 1].Key == 'q') start++;
            else if (vowelFollows && start == 1 && _letters[0].Key == 'g' && _letters[1].Key == 'i') start++;
            var end = start;
            while (end < _letters.Count && _letters[end].IsVowel) end++;
            return (start, end);
        }

        private int TonePosition()
        {
            var (start, end) = Cluster();
            if (start == end) return -1;
            // A marked vowel takes the tone; of ươ, the ơ (người).
            var marked = -1;
            for (var i = start; i < end; i++)
            {
                if (_letters[i].Mark != Mark.None) marked = i;
            }
            if (marked >= 0) return marked;
            var length = end - start;
            if (length == 1) return start;
            if (end < _letters.Count) return end - 1; // a final consonant follows: hoàn, toán, huýt
            if (length == 3) return start + 1;        // ngoài, khuỷu
            var pair = $"{_letters[start].Key}{_letters[start + 1].Key}";
            return options.ModernToneStyle && pair is "oa" or "oe" or "uy" ? start + 1 : start;
        }
    }
}
