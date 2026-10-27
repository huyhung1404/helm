// Port of sea-g2p src/lang/vi/mod.rs (Normalizer::normalize, clean_vietnamese_text_ctx, sanitize_unicode, cleanup).
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Vietnamese text normalization, run before G2P: numbers, dates, units, abbreviations, formulas and URLs become words a
/// voice can read. Three input-shaping passes come first (look-alike Unicode folded, NFC, foreign Latin letters folded),
/// then the staged pipeline of <see cref="Stages"/>, which masks technical spans first and restores them last. The
/// dictionary is only consulted (read-only) to decide how paths, emails and acronyms are read, so one instance serves
/// any number of threads.
/// </summary>
internal sealed partial class VietnameseNormalizer(PhonemeDictionary dict)
{
    [GeneratedRegex(@"[ \t\xA0]+", Rx.Options)]
    private static partial Regex ReExtraSpaces();

    [GeneratedRegex(@",\s*,", Rx.Options)]
    private static partial Regex ReExtraCommas();

    [GeneratedRegex(@"[․‥…]", Rx.Options)]
    private static partial Regex ReEllipsis();

    [GeneratedRegex(@"\.[\s.]*\.", Rx.Options)]
    private static partial Regex ReMultiDot();

    [GeneratedRegex(@"([!?])[!?\s]*[!?]", Rx.Options)]
    private static partial Regex ReMultiBang();

    [GeneratedRegex(@",\s*([.!?;])", Rx.Options)]
    private static partial Regex ReCommaBeforePunct();

    [GeneratedRegex(@"\s+([,.!?;:])", Rx.Options)]
    private static partial Regex ReSpaceBeforePunct();

    [GeneratedRegex(@"([.,!?;:])([^\s\d<])", Rx.Options)]
    private static partial Regex ReMissingSpaceAfterPunct();

    [GeneratedRegex(@"(?i)(?s)(__start_en__.*?__end_en__|<en>.*?</en>)", Rx.Options)]
    private static partial Regex ReInternalEnTag();

    [GeneratedRegex(@"(\d+)\.(\d+)", Rx.Options)]
    private static partial Regex ReDotBetweenDigits();

    [GeneratedRegex(@"(?i)ENTOKEN\d+", Rx.Options)]
    private static partial Regex ReEnToken();

    [GeneratedRegex(@"(?si)<en>.*?</en>", Rx.Options)]
    private static partial Regex ReEnTag();

    [GeneratedRegex(Rx.BL + @"ln" + Rx.BR, Rx.Options)]
    private static partial Regex ReLn();

    /// <summary>camelCase and "&amp;" exceptions ("arXiv", "GD&amp;ĐT"), masked before any splitting pass can break them.</summary>
    private static readonly Regex? ReEarlyExceptions = BuildEarlyExceptions();

    private static Regex? BuildEarlyExceptions()
    {
        var keys = Resources.CombinedExceptions.Keys.Where(k =>
        {
            if (k.Contains('&')) return true;
            var bytes = Encoding.UTF8.GetBytes(k);
            for (var i = 0; i + 1 < bytes.Length; i++)
                if (bytes[i] is >= (byte)'a' and <= (byte)'z' && bytes[i + 1] is >= (byte)'A' and <= (byte)'Z') return true;
            return false;
        }).OrderBy(k => k, StringComparer.Ordinal).Select(Rx.Escape).ToList();
        if (keys.Count == 0) return null;
        return Rx.Build(Rx.BL + "(?:" + string.Join("|", keys) + ")" + Rx.BR);
    }

    /// <summary>
    /// Fold the invisible and look-alike Unicode pasted from Word, the web or a PDF: zero-width boundary marks become a
    /// space, joiners vanish, curly apostrophes and typographic hyphens become ASCII, ℃/℉ expand, every other Unicode
    /// space becomes a space and the remaining control characters are dropped. Newlines and tabs survive.
    /// </summary>
    internal static string SanitizeUnicode(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in RustText.Chars(text))
        {
            var c = RustText.FirstChar(ch);
            switch (c)
            {
                case 0x2018:
                case 0x2019:
                    sb.Append('\'');
                    break;
                case 0x2010:
                case 0x2011:
                    sb.Append('-');
                    break;
                case 0x27E8:
                    sb.Append('(');
                    break;
                case 0x27E9:
                    sb.Append(')');
                    break;
                case 0x2103:
                    sb.Append("°C");
                    break;
                case 0x2109:
                    sb.Append("°F");
                    break;
                case 0x200B:
                case 0x200C:
                case 0x00AD:
                    sb.Append(' ');
                    break;
                case 0x200D:
                case 0x2060:
                case 0xFEFF:
                case 0x034F:
                case 0x180E:
                    break;
                case '\n':
                case '\r':
                case '\t':
                    sb.Append(ch);
                    break;
                default:
                    if (RustText.IsWhitespace(c)) sb.Append(' ');
                    else if (!RustText.IsControl(c)) sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string CleanupWhitespace(string text)
    {
        var res = ReMultiDot().Replace(text, ".");
        res = ReMultiBang().Replace(res, "$1");
        res = ReExtraSpaces().Replace(res, " ");
        res = ReExtraCommas().Replace(res, ",");
        res = ReCommaBeforePunct().Replace(res, "$1");
        res = ReSpaceBeforePunct().Replace(res, "$1");
        res = PunctuationRules.CollapsePunctRuns(res);
        res = ReMissingSpaceAfterPunct().Replace(res, "$1 $2");
        return res.Trim().Trim(',');
    }

    /// <summary>The opaque mask for protected span number <paramref name="index"/>: "mask0000mask" with digits as letters.</summary>
    private static string MaskFor(int index)
    {
        var raw = "mask" + index.ToString("D4", CultureInfo.InvariantCulture) + "mask";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw) sb.Append(c is >= '0' and <= '9' ? (char)(c - '0' + 'a') : c);
        return sb.ToString();
    }

    /// <summary>The staged pipeline, stages 0 to 17.</summary>
    internal string CleanVietnameseTextCtx(string text, bool forceVi)
    {
        var maskMap = new List<(string Mask, string Original)>();
        var ctx = Stages.DetectContext(text, forceVi);

        string Protect(string original)
        {
            var mask = MaskFor(maskMap.Count);
            maskMap.Add((mask, original));
            return mask;
        }

        // Stage 1: protect spans. Each is normalized to its final wording now and replaced by an opaque mask.
        var current = ReEnToken().Replace(text, m => Protect(RustText.ToLower(m.Value)));
        current = Technical.ReEmail().Replace(current, m => Protect(Technical.NormalizeEmails(dict, m.Value, ctx.ViCtx, ctx.EnCtx)));
        current = Technical.ReTechnical().Replace(current, m =>
        {
            var orig = m.Value;
            if (orig.Contains('-') && orig.All(c => RustText.IsAsciiAlpha(c) || c == '-') && orig.Any(RustText.IsAsciiLower))
                return orig.Replace('-', ' ');
            var val = !ctx.EnCtx && Misc.ReAcronymsExceptions.IsMatch(orig)
                ? Resources.CombinedExceptions.GetValueOrDefault(orig, orig)
                : Technical.NormalizeTechnical(dict, orig, ctx.ViCtx, ctx.EnCtx);
            return Protect(val);
        });
        if (ReEarlyExceptions != null)
            current = ReEarlyExceptions.Replace(current, m => Protect(Resources.CombinedExceptions.GetValueOrDefault(m.Value, m.Value)));

        current = Stages.EarlyLexical(current, ctx);
        current = Stages.Superscripts(current);
        current = Stages.Math(current, ctx);
        current = Stages.SplitConcatenatedTerms(current);
        current = Stages.CodesStage(current, ctx);
        current = Stages.Arithmetic(current);
        current = Stages.Abbreviations(current);
        current = Stages.PercentScores(current);

        current = DatesTimes.NormalizeDate(current);
        current = DatesTimes.NormalizeTime(current);

        current = Stages.Phones(current);
        current = Stages.RangesSigns(current);
        current = Stages.UnitsStage(current, ctx);
        current = Stages.Decimals(current);
        current = Stages.Residual(dict, current, ctx);

        // Stage 16: letters.
        current = ReInternalEnTag().Replace(current, m => Protect(m.Value));
        if (!ctx.EnCtx) current = Misc.ExpandStandaloneLetters(current);
        if (current.Contains('.'))
            current = ReDotBetweenDigits().Replace(current, m => m.V(1) + " chấm " + m.V(2));

        // Stage 17: finalize. Nothing may rewrite text after this point.
        foreach (var (mask, original) in maskMap)
        {
            current = current.Replace(mask, original, StringComparison.Ordinal);
            current = current.Replace(RustText.ToLower(mask), original, StringComparison.Ordinal);
        }
        current = current.Replace("__start_en__", "<en>", StringComparison.Ordinal)
            .Replace("__end_en__", "</en>", StringComparison.Ordinal);
        current = current.Replace('_', ' ').Replace('-', ' ');
        current = CleanupWhitespace(current);
        return RustText.ToLower(current);
    }

    private static string Nfc(string s)
    {
        try
        {
            return s.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return s;
        }
    }

    /// <summary><c>Normalizer("vi").normalize(text, punc_norm)</c>.</summary>
    public string Normalize(string text, bool puncNorm)
    {
        if (text.Length == 0) return "";

        var composed = Nfc(SanitizeUnicode(text));
        var folded = Translit.FoldForeignLetters(composed);
        var current = ReEllipsis().Replace(folded, ".");
        current = ReLn().Replace(current, "log");

        var hadMath = RustText.ToLower(current).Contains("<math>", StringComparison.Ordinal);
        if (hadMath)
        {
            current = InlineMath.ReMathTag().Replace(current, m =>
            {
                var inner = InlineMath.SplitMathLetters(m.V(1));
                inner = InlineMath.ReMathFactorial().Replace(inner, " giai thừa ");
                inner = InlineMath.ReMathBinMinus().Replace(inner, " trừ ");
                inner = InlineMath.ReLeadNeg().Replace(inner.TrimStart(), "âm $1", 1);
                return " " + inner + " ";
            });
        }

        var enContents = new List<string>();
        current = ReEnTag().Replace(current, m =>
        {
            enContents.Add(m.Value);
            return "ENTOKEN" + (enContents.Count - 1).ToString(CultureInfo.InvariantCulture);
        });

        current = CleanVietnameseTextCtx(current, hadMath);
        current = ReExtraSpaces().Replace(current, " ").Trim();

        for (var idx = 0; idx < enContents.Count; idx++)
        {
            var placeholder = "entoken" + idx.ToString(CultureInfo.InvariantCulture);
            current = current.Replace(placeholder, enContents[idx], StringComparison.Ordinal);
        }

        var result = ReExtraSpaces().Replace(current, " ").Trim();
        return puncNorm ? PunctuationRules.ApplyPuncNorm(result) : result;
    }
}
