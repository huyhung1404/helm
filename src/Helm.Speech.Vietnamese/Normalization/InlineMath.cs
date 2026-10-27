// Port of sea-g2p src/lang/vi/mod.rs (the formula passes: <math> regions and bare formulas in prose).
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Formulas. Inside an author's <c>&lt;math&gt;</c> region every letter cluster except a function name is split into
/// letters so they are read by name ("4ac" becomes "bốn a xê"). In ordinary prose, runs of maths-shaped tokens get the
/// same treatment only on strong evidence (an "=", a radical, a super/subscript), because prose and formulas share an
/// alphabet: "F = ma" takes "ma", "khi x = 1" leaves "khi" alone.
/// </summary>
internal static partial class InlineMath
{
    [GeneratedRegex(@"(?si)<math>(.*?)</math>", Rx.Options)]
    internal static partial Regex ReMathTag();

    [GeneratedRegex(@"[a-zA-Z]+", Rx.Options)]
    private static partial Regex ReMathWord();

    [GeneratedRegex(@"^[-–—]\s*([a-zA-Z0-9])", Rx.Options)]
    internal static partial Regex ReLeadNeg();

    [GeneratedRegex(@"(?<=[0-9A-Za-z)])!", Rx.Options)]
    internal static partial Regex ReMathFactorial();

    [GeneratedRegex(@"(?<=[0-9A-Za-z)])\s*[-–—]\s*(?=[0-9A-Za-z(∫√])", Rx.Options)]
    internal static partial Regex ReMathBinMinus();

    private static readonly HashSet<string> MathFuncs = new(
    [
        "sin", "cos", "tan", "cot", "sec", "csc", "sinh", "cosh", "tanh", "coth",
        "arcsin", "arccos", "arctan", "asin", "acos", "atan", "log", "ln", "lg",
        "exp", "lim", "max", "min", "sup", "inf", "det", "dim", "deg", "gcd",
        "lcm", "mod", "arg", "sgn", "rank", "tr",
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> MathDiffs = new(["dx", "dy", "dz", "du", "dv", "dt"], StringComparer.Ordinal);

    private static string SpaceOut(string w) => string.Join(" ", RustText.Chars(w));

    /// <summary>Inside &lt;math&gt;: split every letter cluster but single letters and function names.</summary>
    public static string SplitMathLetters(string content) => ReMathWord().Replace(content, m =>
    {
        var w = m.Value;
        return RustText.CharCount(w) == 1 || MathFuncs.Contains(RustText.ToLower(w)) ? w : SpaceOut(w);
    });

    private static bool IsStrongMathChar(char c) =>
        c is '=' or '√' or '∫' or '±' or '≤' or '≥' or '≠'
        || Resources.SuperscriptsMap.ContainsKey(c) || Resources.SubscriptsMap.ContainsKey(c);

    private static bool HasOperatorChar(string tok) =>
        tok.Any(c => c is '=' or '+' or '-' or '–' or '—' or '±' or '*' or '×' or '÷');

    private const string AllowedSymbols = "()[]{}+-–—*/=±≤≥≠≈×÷√∫|!'^.,;:°½¼¾⅓⅔∆∑";
    private const string EvidenceSymbols = "+-–—*/=±≤≥≠≈×÷√∫^'!½¼¾⅓⅔∆∑";

    private static bool IsGreek(int c) => c is >= 'α' and <= 'ω' or >= 'Α' and <= 'Ω';

    private static bool IsScript(int c) =>
        c < 0x10000 && (Resources.SuperscriptsMap.ContainsKey((char)c) || Resources.SubscriptsMap.ContainsKey((char)c));

    /// <summary>(passes the character test, is a multi-letter word that needs an operator beside it).</summary>
    private static (bool Base, bool Conditional) ClassifyMathToken(string tok)
    {
        if (tok.Length == 0) return (false, false);
        bool Allowed(int c) => c < 0x80 && RustText.IsAsciiAlnum((char)c)
            || c < 0x10000 && AllowedSymbols.Contains((char)c) || IsGreek(c) || IsScript(c);
        if (!RustText.All(tok, Allowed)) return (false, false);

        var runs = new List<string>();
        var run = "";
        foreach (var c in tok)
        {
            if (RustText.IsAsciiAlpha(c)) run += c;
            else if (run.Length > 0)
            {
                runs.Add(run);
                run = "";
            }
        }
        if (run.Length > 0) runs.Add(run);
        foreach (var r in runs)
            if (r.Length > 3 && !MathFuncs.Contains(RustText.ToLower(r))) return (false, false);

        var hasEvidence = RustText.Any(tok, c => c is >= '0' and <= '9' || c < 0x10000 && EvidenceSymbols.Contains((char)c)
            || IsGreek(c) || IsScript(c));
        var pureAlphaMulti = runs.Count == 1 && runs[0].Length >= 2 && !hasEvidence
            && !MathFuncs.Contains(RustText.ToLower(runs[0])) && !MathDiffs.Contains(RustText.ToLower(runs[0]));
        return (true, pureAlphaMulti);
    }

    private static string SplitMathToken(string tok, bool protectUnits) => ReMathWord().Replace(tok, m =>
    {
        var w = m.Value;
        var lower = RustText.ToLower(w);
        if (RustText.CharCount(w) == 1 || MathFuncs.Contains(lower) || (protectUnits && Resources.MeasurementKeyVi.ContainsKey(lower)))
            return w;
        return SpaceOut(w);
    });

    /// <summary>Find runs of maths-shaped tokens per line and read them as a formula.</summary>
    public static string ExpandInlineMath(string text) => string.Join("\n", text.Split('\n').Select(line =>
    {
        var toks = RustText.SplitWhitespace(line);
        if (toks.Length < 2) return line;
        var classes = toks.Select(ClassifyMathToken).ToArray();
        var mathy = new bool[toks.Length];
        for (var i = 0; i < toks.Length; i++)
        {
            var (b, conditional) = classes[i];
            if (!b) continue;
            if (!conditional)
            {
                mathy[i] = true;
                continue;
            }
            var prevOp = i > 0 && classes[i - 1].Base && HasOperatorChar(toks[i - 1]);
            var nextOp = i + 1 < toks.Length && classes[i + 1].Base && HasOperatorChar(toks[i + 1]);
            mathy[i] = prevOp || nextOp;
        }

        var output = new List<string>(toks.Length);
        var k = 0;
        while (k < toks.Length)
        {
            if (!mathy[k])
            {
                output.Add(toks[k]);
                k++;
                continue;
            }
            var j = k;
            while (j < toks.Length && mathy[j]) j++;
            var span = string.Join(" ", toks[k..j]);
            var strong = span.Any(IsStrongMathChar);
            var strongNonEq = span.Any(c => c != '=' && IsStrongMathChar(c));
            if ((j - k >= 2 && strong) || (j - k == 1 && strongNonEq))
            {
                var parts = new List<string>(j - k);
                for (var t = k; t < j; t++)
                {
                    var prevNumeric = t == k || toks[t - 1].Any(RustText.IsAsciiDigit);
                    parts.Add(SplitMathToken(toks[t], prevNumeric));
                }
                var s = string.Join(" ", parts);
                s = ReMathFactorial().Replace(s, " giai thừa ");
                s = ReMathBinMinus().Replace(s, " trừ ");
                output.Add(s);
            }
            else
            {
                output.Add(span);
            }
            k = j;
        }
        return string.Join(" ", output);
    }));
}
