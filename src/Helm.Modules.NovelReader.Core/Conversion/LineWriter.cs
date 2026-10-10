using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Conversion;

/// <summary>
/// Joins translated pieces into Vietnamese the way QuickTranslator's appendTranslatedWord does: words separated by
/// spaces, none before closing punctuation or after an opening quote, a capital letter at the start of a line and of
/// each sentence (also inside a quote that opens after a colon). Words that translate to nothing ("了") vanish.
/// </summary>
internal static class LineWriter
{
    /// <summary>A translated piece before spacing: its place in the source, its text and kind.</summary>
    public readonly record struct Piece(int Start, int Length, string Text, SegmentKind Kind);

    private const string Closing = ",.!?:;)]}”’》%~…";
    private const string Opening = "“‘([{《";

    public static List<Segment> Write(string source, IReadOnlyList<Piece> pieces)
    {
        var segments = new List<Segment>(pieces.Count);
        var started = false;      // something visible was written
        var lastChar = '\0';      // the last visible character written
        var afterOpening = false; // the last thing written opens a quote or a bracket
        var lastWasWord = false;  // the last thing written was a translated word
        var capitalize = true;    // the next word starts a sentence
        var afterColon = false;   // a colon was written; a quote after it starts a sentence
        var doubleOpen = false;   // a straight " is open
        var singleOpen = false;   // a straight ' is open

        string SpaceBefore() => started && lastChar != ' ' && !afterOpening && lastChar != '—' ? " " : "";

        for (var p = 0; p < pieces.Count; p++)
        {
            var piece = pieces[p];
            if (piece.Kind == SegmentKind.Text)
            {
                var c = piece.Text[0];
                var before = "";
                var opening = false;
                if (c == ' ')
                {
                    if (!started || lastChar == ' ' || afterOpening) { segments.Add(new Segment(piece.Start, piece.Length, "", piece.Kind, "")); continue; }
                }
                else if (c == '"' || (c == '\'' && !IsApostrophe(source, piece.Start, lastChar)))
                {
                    ref var open = ref c == '"' ? ref doubleOpen : ref singleOpen;
                    if (!open) { before = SpaceBefore(); opening = true; }
                    open = !open;
                }
                else if (Opening.Contains(c))
                {
                    before = SpaceBefore();
                    opening = true;
                }
                else if (char.IsLetterOrDigit(c))
                {
                    var sentenceGap = lastChar is ',' or '!' or '?' or ';' or ')' or ']' or '”' or '’'
                        || (lastChar is '.' or ':' && !char.IsAsciiDigit(PreviousSourceChar(source, piece.Start, 2)));
                    if (lastWasWord || sentenceGap) before = SpaceBefore();
                    capitalize = false;
                    afterColon = false;
                }
                segments.Add(new Segment(piece.Start, piece.Length, piece.Text, piece.Kind, before));
                if (c == ' ') { lastChar = ' '; lastWasWord = false; afterOpening = false; continue; }

                // Sentence state.
                if (c is '!' or '?') capitalize = true;
                else if (c == '.') capitalize = !IsPartOfEllipsis(source, piece.Start);
                else if (c == ':') afterColon = true;
                else if (c is ',' or ';') afterColon = false;
                if (opening && afterColon) capitalize = true;
                started = true;
                lastChar = c;
                afterOpening = opening;
                lastWasWord = false;
                continue;
            }

            var text = piece.Text;
            if (text.Length == 0)
            {
                // A word that translates to nothing: keep it clickable but invisible.
                segments.Add(new Segment(piece.Start, piece.Length, "", piece.Kind, ""));
                continue;
            }
            var space = Closing.Contains(text[0]) ? "" : SpaceBefore();
            if (capitalize) text = ChineseText.Capitalize(text);
            segments.Add(new Segment(piece.Start, piece.Length, text, piece.Kind, space));
            if (text.Any(char.IsLetter))
            {
                capitalize = false;
                afterColon = false;
            }
            var trimmed = text.TrimEnd();
            var last = trimmed.Length > 0 ? trimmed[^1] : ' ';
            if (last is '.' or '!' or '?') capitalize = !text.EndsWith("..", StringComparison.Ordinal);
            // A phrase can end in its own colon ("道: " → "nói:"); a quote after it starts a sentence.
            else if (last == ':') afterColon = true;
            started = true;
            lastChar = last;
            afterOpening = false;
            lastWasWord = true;
        }
        return segments;
    }

    /// <summary>A ' between two Latin letters ("don't") is an apostrophe, not a quote.</summary>
    private static bool IsApostrophe(string source, int at, char lastChar) =>
        char.IsAsciiLetter(lastChar) && at + 1 < source.Length && char.IsAsciiLetter(source[at + 1]);

    private static bool IsPartOfEllipsis(string source, int at) =>
        (at > 0 && source[at - 1] == '.') || (at + 1 < source.Length && source[at + 1] == '.');

    /// <summary>The character <paramref name="back"/> places before <paramref name="at"/> in the source.</summary>
    private static char PreviousSourceChar(string source, int at, int back) => at - back >= 0 ? source[at - back] : '\0';
}
