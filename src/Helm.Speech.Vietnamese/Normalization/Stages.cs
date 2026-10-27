// Port of sea-g2p src/lang/vi/mod.rs (the stage_* functions of the staged pipeline).
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>Language flags computed once from the raw input and read by every later stage.</summary>
/// <param name="ViCtx">The sentence has diacritic Vietnamese letters: paths and URLs are read the Vietnamese way.</param>
/// <param name="EnCtx">The sentence is pure English: numbers and symbols are read in English.</param>
internal readonly record struct SentenceContext(bool ViCtx, bool EnCtx);

/// <summary>
/// The numbered stages of the Vietnamese normalizer. The order is a contract: each stage assumes the earlier ones have
/// resolved a class of ambiguity (codes before the multiplication pass, scores before dates, dates and phones before
/// ranges, units once every sign is settled), so the pipeline in <see cref="VietnameseNormalizer"/> runs them exactly in
/// sea-g2p's order.
/// </summary>
internal static partial class Stages
{
    [GeneratedRegex(Rx.BL + @"[a-zA-Z]{2,}" + Rx.BR, Rx.Options)]
    private static partial Regex ReWordish();

    /// <summary>
    /// Stage 0: English mode needs no Vietnamese diacritics and either three word-like tokens with lowercase letters or
    /// two genuine top-list English words. <paramref name="forceVi"/> (a &lt;math&gt; region) disables it.
    /// </summary>
    public static SentenceContext DetectContext(string text, bool forceVi)
    {
        var viCtx = RustText.Any(text, c => RustText.IsAlphabetic(c) && c >= 0x80);
        var lowercaseWordish = 0;
        var dictionaryWords = 0;
        foreach (Match m in ReWordish().Matches(text))
        {
            var w = m.Value;
            if (lowercaseWordish < 3 && w.Any(RustText.IsAsciiLower)) lowercaseWordish++;
            if (dictionaryWords < 2 && w.All(RustText.IsAsciiLower) && WordLists.EnTopWords.Contains(w)) dictionaryWords++;
        }
        var enCtx = !forceVi && !viCtx && (lowercaseWordish >= 3 || dictionaryWords >= 2);
        return new SentenceContext(viCtx, enCtx);
    }

    /// <summary>Stage 2: weekday abbreviations, "text2text", then English pre-normalization for English sentences.</summary>
    public static string EarlyLexical(string text, SentenceContext ctx)
    {
        var output = Codes.ExpandWeekdayAbbr(text);
        output = Num2En.ExpandSandwichDigits(output);
        if (ctx.EnCtx) output = Num2En.EnglishPrenormalize(output);
        return output;
    }

    [GeneratedRegex(Rx.BL + @"(km|cm|mm|m)²", Rx.Options)]
    private static partial Regex ReSupUnit();

    [GeneratedRegex(Rx.BL + @"(km|cm|mm|m)³", Rx.Options)]
    private static partial Regex ReSupUnit3();

    [GeneratedRegex(@"([⁻⁺])([⁰¹²³⁴⁵⁶⁷⁸⁹]+)", Rx.Options)]
    private static partial Regex ReSuperscriptSigned();

    [GeneratedRegex(@"[⁰¹²³⁴⁵⁶⁷⁸⁹]{2,}", Rx.Options)]
    private static partial Regex ReSuperscriptRun();

    private static string SupDigits(string s) => string.Concat(s.Select(c => c switch
    {
        '⁰' => '0', '¹' => '1', '²' => '2', '³' => '3', '⁴' => '4',
        '⁵' => '5', '⁶' => '6', '⁷' => '7', '⁸' => '8', _ => '9',
    }));

    /// <summary>Stage 3: "m²" to "m2", signed exponents ("10⁻³"), then runs of superscripts read as one exponent.</summary>
    public static string Superscripts(string text)
    {
        var output = ReSupUnit().Replace(text, "${1}2");
        output = ReSupUnit3().Replace(output, "${1}3");
        output = ReSuperscriptSigned().Replace(output, m =>
        {
            var sign = m.V(1) == "⁻" ? "trừ " : "";
            return " mũ " + sign + Num2Vi.N2W(SupDigits(m.V(2))) + " ";
        });
        return ReSuperscriptRun().Replace(output, m => " mũ " + Num2Vi.N2W(SupDigits(m.Value)) + " ");
    }

    [GeneratedRegex(@"(?<=" + Rx.BL + @"[0-9a-zA-Z])!(?=\s*[)=/,(+×*-])", Rx.Options)]
    private static partial Regex ReFactorialInline();

    [GeneratedRegex(@"(?<=\s)[-–—](?=(?:sin|cos|tan|cot|log|ln|lim)" + Rx.BR + @")", Rx.Options)]
    private static partial Regex ReNegFunc();

    [GeneratedRegex(Rx.BL + @"([a-z])\s+[-–—]\s+([a-z])" + Rx.BR, Rx.Options)]
    private static partial Regex ReMinusSingleVars();

    /// <summary>Stage 4: bare formulas, factorials, "-sin x", "x - a". Skipped for English sentences.</summary>
    public static string Math(string text, SentenceContext ctx)
    {
        if (ctx.EnCtx) return text;
        var output = InlineMath.ExpandInlineMath(text);
        output = ReFactorialInline().Replace(output, " giai thừa ");
        output = ReNegFunc().Replace(output, " âm ");
        return ReMinusSingleVars().Replace(output, "$1 trừ $2");
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", Rx.Options)]
    private static partial Regex ReCamelCase();

    [GeneratedRegex(Rx.BL + @"[a-zA-Z]{3,}" + Rx.BR, Rx.Options)]
    private static partial Regex RePotentialConcat();

    /// <summary>Stage 5: split camelCase, keeping chemical formulas, acronyms and camelCase units (kWh) whole.</summary>
    public static string SplitConcatenatedTerms(string text) => RePotentialConcat().Replace(text, m =>
    {
        var word = m.Value;
        var isProtected = word.Any(RustText.IsAsciiLower)
            ? Resources.IsChemicalFormula(word)
            : Acronyms.ReAcronym.IsMatch(word);
        if (isProtected || Resources.MeasurementKeyVi.ContainsKey(RustText.ToLower(word))) return word;
        return ReCamelCase().Replace(word, " ");
    });

    [GeneratedRegex(Rx.BL + @"(\d)(?!H\d{2}" + Rx.BR + @")(?=[A-Z][a-z]|[A-Z][A-Z]?\d|[A-Z][A-Z][a-z])", Rx.Options)]
    private static partial Regex ReChemDigitPrefix();

    /// <summary>Stage 6: licence plates, codes, and the coefficient before a chemical formula ("6CO2").</summary>
    public static string CodesStage(string text, SentenceContext ctx)
    {
        if (ctx.EnCtx) return text;
        var output = Codes.ExpandCodesAndPlates(text);
        return ReChemDigitPrefix().Replace(output, "$1 ");
    }

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(bằng|tính|kết quả)\s+(\d+(?:[.,]\d+)?)\s*[-–—]\s*(\d+(?:[.,]\d+)?)" + Rx.BR, Rx.Options)]
    private static partial Regex ReContextTru();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(\d+(?:[.,]\d+)?)\s*[-–—]\s*(\d+(?:[.,]\d+)?)\s+(bằng|tính|kết quả)" + Rx.BR, Rx.Options)]
    private static partial Regex ReContextTruPost();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(từ|khoảng|trong)\s+(\d+(?:[.,]\d+)?)\s*[-–—]\s*(\d+(?:[.,]\d+)?)" + Rx.BR, Rx.Options)]
    private static partial Regex ReContextDen();

    [GeneratedRegex(@"([\d./]+)\s*[-–—]\s*([\d./]+)\s*=", Rx.Options)]
    private static partial Regex ReEqMinus();

    [GeneratedRegex(@"=\s*[-–—](\d+(?:[./]\d+)?)", Rx.Options)]
    private static partial Regex ReEqNeg();

    /// <summary>Stage 7: powers, multiplication, then "trừ" or "đến" for a dash with an arithmetic or range cue.</summary>
    public static string Arithmetic(string text)
    {
        var output = Units.ExpandPowerOfTen(text);
        output = Numerical.ReMultiply().Replace(output, m => Numerical.ExpandMultiplyNumber(m.Value));
        output = ReContextTru().Replace(output, " $1 $2 trừ $3 ");
        output = ReContextTruPost().Replace(output, " $1 trừ $2 $3 ");
        output = ReContextDen().Replace(output, " $1 $2 đến $3 ");
        output = ReEqMinus().Replace(output, m => m.V(1) + " trừ " + m.V(2) + " =");
        return ReEqNeg().Replace(output, m => "= âm " + m.V(1));
    }

    [GeneratedRegex(Rx.BL + @"([PQĐ])\.\s*(?=\d)", Rx.Options)]
    private static partial Regex ReAddrAbbr();

    [GeneratedRegex(Rx.BL + @"(\d{1,4})tr(\d)?(?!" + Rx.W + @")", Rx.Options)]
    private static partial Regex ReMoneyTr();

    /// <summary>Stage 8: abbreviations, "P.5"/"Q.1"/"Đ.3/2", "1tr5", scientific notation.</summary>
    public static string Abbreviations(string text)
    {
        var output = Misc.ExpandAbbreviations(text);
        output = ReAddrAbbr().Replace(output, m => m.V(1) switch
        {
            "P" => " phường ",
            "Q" => " quận ",
            _ => " đường ",
        });
        output = ReMoneyTr().Replace(output, m =>
        {
            var x = Num2Vi.N2W(m.V(1));
            var y = m.G(2);
            return y != null ? " " + x + " triệu " + Num2Vi.N2W(y) + " trăm nghìn " : " " + x + " triệu ";
        });
        return Units.ExpandScientificNotation(output);
    }

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*[-–—]\s*(\d+(?:[.,]\d+)?)\s*%", Rx.Options)]
    private static partial Regex ReRangePct();

    [GeneratedRegex(@"(?<![\d.,])[-–—]\s?(\d+(?:[.,]\d+)?)\s*%", Rx.Options)]
    private static partial Regex RePctNeg();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(thắng|thua|hòa|hoà|tỉ số|tỷ số|chung cuộc|đánh bại|cầm hòa|cầm hoà)\s+(\d{1,2})\s*[-–—]\s*(\d{1,2})" + Rx.BR, Rx.Options)]
    private static partial Regex ReScoreKw();

    [GeneratedRegex(@"(\p{Lu}\p{L}+(?:\s\p{Lu}\p{L}+)?)\s(\d{1,2})\s*[-–—]\s*(\d{1,2})\s(\p{Lu}\p{L}+)", Rx.Options)]
    private static partial Regex ReScoreTeams();

    private static readonly string[] ScoreExclude =
    [
        "điều", "khoản", "chương", "mục", "phần", "điểm", "tiết", "tập", "quyển",
        "hồi", "kỳ", "kì", "quý", "tháng", "ngày", "năm", "tuần", "từ", "khoảng",
        "trong", "bài", "câu", "trang", "dòng", "khóa", "khoá",
    ];

    /// <summary>Stage 9: percentage ranges and negatives, then sport scores read as two numbers.</summary>
    public static string PercentScores(string text)
    {
        var output = ReRangePct().Replace(text, "$1 đến $2%");
        output = RePctNeg().Replace(output, " âm $1% ");
        output = ReScoreKw().Replace(output, m => m.V(1) + " " + Num2Vi.N2W(m.V(2)) + " " + Num2Vi.N2W(m.V(3)));
        return ReScoreTeams().Replace(output, m =>
        {
            var team1 = m.V(1);
            var space = team1.LastIndexOf(' ');
            var last = RustText.ToLower(space >= 0 ? team1[(space + 1)..] : team1);
            if (ScoreExclude.Contains(last)) return m.Value;
            return team1 + " " + Num2Vi.N2W(m.V(2)) + " " + Num2Vi.N2W(m.V(3)) + " " + m.V(4);
        });
    }

    [GeneratedRegex(Rx.BL + @"\d+(?:[–\-—]\d+){2,}" + Rx.BR, Rx.Options)]
    private static partial Regex ReNumericDashGroups();

    [GeneratedRegex(@"(?<![\d.,])\(?(?:\+84\s?|0)\d{1,3}\)?\s\d{3,4}\s\d{3,4}(?!\d)", Rx.Options)]
    private static partial Regex ReLandline();

    [GeneratedRegex(Rx.BL + @"0\d{2,3}(?:\s\d{3}){2}" + Rx.BR, Rx.Options)]
    private static partial Regex RePhoneSpace();

    [GeneratedRegex(Rx.BL + @"(0\d{2,3})[–\-—](\d{3,4})[–\-—](\d{4})" + Rx.BR, Rx.Options)]
    private static partial Regex RePhoneWithDash();

    [GeneratedRegex(Rx.BL + @"(1800|1900)[\s.\-–—]?(\d{3,6})" + Rx.BR, Rx.Options)]
    private static partial Regex ReHotline();

    [GeneratedRegex(@"[^0-9]+", Rx.Options)]
    private static partial Regex ReNonAsciiDigits();

    /// <summary>Stage 11: phone numbers, hotlines and dash-separated groups, read figure by figure.</summary>
    public static string Phones(string text)
    {
        var output = ReNumericDashGroups().Replace(text, m =>
            string.Join(", ", m.Value.Split(['-', '–', '—']).Select(Num2Vi.N2WSingle)));

        output = ReLandline().Replace(output, m =>
        {
            var matched = m.Value;
            var groups = ReNonAsciiDigits().Split(matched).Where(s => s.Length > 0).Select(Num2Vi.N2WSingle);
            var prefix = matched.Contains('+') ? "cộng " : "";
            return " " + prefix + string.Join(", ", groups) + " ";
        });

        output = RePhoneSpace().Replace(output, m =>
            string.Join(", ", RustText.SplitWhitespace(m.Value).Select(Num2Vi.N2WSingle)));

        output = RePhoneWithDash().Replace(output, m =>
            " " + Num2Vi.N2WSingle(m.V(1)) + ", " + Num2Vi.N2WSingle(m.V(2)) + ", " + Num2Vi.N2WSingle(m.V(3)) + " ");

        return ReHotline().Replace(output, m => " " + Num2Vi.N2WSingle(m.V(1)) + " " + Num2Vi.N2WSingle(m.V(2)) + " ");
    }

    [GeneratedRegex(Rx.BL + @"10\^([-+]?\d+)" + Rx.BR, Rx.Options)]
    private static partial Regex RePowerOfTenImplicit();

    [GeneratedRegex(@"(?<!\d)(?<!\d[,.])(?<![a-zA-Z])(\d{1,15}(?:[,.]\d{1,15})?)(\s*)[–\-—](\s*)(\d{1,15}(?:[,.]\d{1,15})?)(?!\d)(?![.,]\d)(?![a-zA-Z])", Rx.Options)]
    private static partial Regex ReRange();

    [GeneratedRegex(@"(?<=[(\[{=±+*/×÷])[-–—]((?:sin|cos|tan|cot|log|ln|lim)" + Rx.BR + @"|[a-zA-Z]" + Rx.BR + @")", Rx.Options)]
    private static partial Regex ReNegVar1();

    [GeneratedRegex(@"(?<=[(\[{=±+*/×÷]\s)[-–—]((?:sin|cos|tan|cot|log|ln|lim)" + Rx.BR + @"|[a-zA-Z]" + Rx.BR + @")", Rx.Options)]
    private static partial Regex ReNegVar2();

    [GeneratedRegex(@"(?<=[²³⁴⁵⁶⁷⁸⁹ⁿⁱ])\s*[-–—]\s*", Rx.Options)]
    private static partial Regex ReMathMinusSup();

    [GeneratedRegex(@"\s[-–—]\s(?=\d+[a-zA-Z])", Rx.Options)]
    private static partial Regex ReMathMinusCoef();

    [GeneratedRegex(@"(?<=\d[a-zA-Z])\s[-–—]\s(?=\d)", Rx.Options)]
    private static partial Regex ReMathMinusCoefL();

    [GeneratedRegex(@"(?<=\s)[–\-—](?=\s)", Rx.Options)]
    private static partial Regex ReDashToComma();

    [GeneratedRegex(@"\s*(?:->|=>)\s*", Rx.Options)]
    private static partial Regex ReToSang();

    private static int DigitCount(string s) =>
        RustText.Utf8Len(s.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal));

    /// <summary>Stage 12: "10^x", numeric ranges (similar digit counts), signs, formula minus, dash to comma, "->".</summary>
    public static string RangesSigns(string text)
    {
        var output = RePowerOfTenImplicit().Replace(text, m =>
        {
            var exp = m.V(1);
            return exp.StartsWith('-')
                ? "mười mũ trừ " + Num2Vi.N2W(exp[1..])
                : "mười mũ " + Num2Vi.N2W(exp.Replace("+", "", StringComparison.Ordinal));
        });

        output = ReRange().Replace(output, m =>
        {
            var n1 = m.V(1);
            var spaceBefore = m.V(2);
            var spaceAfter = m.V(3);
            var n2 = m.V(4);
            if (spaceBefore.Length > 0 && spaceAfter.Length == 0) return m.Value;
            return System.Math.Abs(DigitCount(n1) - DigitCount(n2)) <= 1 ? " " + n1 + " đến " + n2 + " " : " " + n1 + " " + n2 + " ";
        });

        output = ReNegVar1().Replace(output, " âm $1 ");
        output = ReNegVar2().Replace(output, " âm $1 ");
        output = ReMathMinusSup().Replace(output, " trừ ");
        output = ReMathMinusCoef().Replace(output, " trừ ");
        output = ReMathMinusCoefL().Replace(output, " trừ ");
        output = ReDashToComma().Replace(output, ",");
        return ReToSang().Replace(output, " sang ");
    }

    [GeneratedRegex(@"(?<!\d)(?<!\d[,.])([-–—]?)(\d{7,})(?!\d)(?![.,]\d)", Rx.Options)]
    private static partial Regex ReLongNum();

    /// <summary>Stage 13: units and currencies, then leftover long digit runs read figure by figure.</summary>
    public static string UnitsStage(string text, SentenceContext ctx)
    {
        var output = Units.ExpandScientificNotation(text);
        output = Units.ExpandHeightWeight(output);
        output = Codes.ExpandSizeLabels(output, ctx.EnCtx);
        output = Units.ExpandCompoundUnits(output);
        output = Units.ExpandUnitsAndCurrency(output);
        output = ReLongNum().Replace(output, m =>
        {
            var sign = m.V(1).Length == 0 ? "" : "âm ";
            return " " + sign + Num2Vi.N2WSingle(m.V(2)) + " ";
        });
        return Units.FixEnglishStyleNumbers(output);
    }

    [GeneratedRegex(Rx.BL + @"(\d+(?:,\d+){2,})" + Rx.BR, Rx.Options)]
    private static partial Regex ReMultiComma();

    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d{3})*),(\d+)(%)?", Rx.Options)]
    private static partial Regex ReFloatWithComma();

    [GeneratedRegex(@"(?<![\d.])\d+(?:\.\d{3})+(?![\d.])", Rx.Options)]
    private static partial Regex ReStripDotSep();

    /// <summary>Stage 14: decimal commas and thousand separators, the last numeric step.</summary>
    public static string Decimals(string text)
    {
        var output = ReMultiComma().Replace(text, m => string.Join(" phẩy ", m.V(1).Split(',').Select(Num2Vi.N2WDecimal)));
        output = ReFloatWithComma().Replace(output, m =>
        {
            var intPart = Num2Vi.N2W(m.V(1).Replace(".", "", StringComparison.Ordinal));
            var dec = m.V(2).TrimEnd('0');
            var res = dec.Length == 0 ? intPart : intPart + " phẩy " + Num2Vi.N2WDecimal(dec);
            if (m.G(3) != null) res += " phần trăm";
            return " " + res + " ";
        });
        return ReStripDotSep().Replace(output, m => m.Value.Replace(".", "", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"(?i)" + Rx.BL + @"âm\s+âm\s+(\S+)", Rx.Options)]
    private static partial Regex ReDoubleAm();

    /// <summary>Stage 15: leftover symbols and numbers, then "âm âm" collapsed when a number follows.</summary>
    public static string Residual(PhonemeDictionary dict, string text, SentenceContext ctx)
    {
        var output = Misc.NormalizeOthers(dict, text, ctx.EnCtx);
        output = Numerical.NormalizeNumberVi(output);
        return ReDoubleAm().Replace(output, m =>
        {
            var next = m.V(1);
            return DatesTimes.NumWords.Contains(RustText.ToLower(next)) ? "âm " + next : m.Value;
        });
    }
}
