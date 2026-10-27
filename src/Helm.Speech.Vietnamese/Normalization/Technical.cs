// Port of sea-g2p src/lang/vi/technical.rs.
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// URLs, file paths and email addresses (used by pipeline stage 1), read in the language of the sentence: inside a
/// Vietnamese sentence the separators are "gạch chéo", "chấm", "a còng", and toneless run-together identifiers
/// ("thongbao", "truongminhkhai") are split into syllables by dynamic programming over frequent syllables, two-syllable
/// compounds and common English words. A token the phoneme dictionary already knows is never split.
/// </summary>
internal static partial class Technical
{
    public static bool DictHas(PhonemeDictionary dict, string word) =>
        dict.LookupMerged(word) != null || dict.LookupCommon(word) != null;

    /// <summary>Known as Vietnamese: a merged entry not tagged English, or any common entry.</summary>
    public static bool DictHasVi(PhonemeDictionary dict, string word) =>
        dict.LookupMerged(word) is { } p && !p.StartsWith("<en>", StringComparison.Ordinal) || dict.LookupCommon(word) != null;

    public static bool DictHasEn(PhonemeDictionary dict, string word) => dict.HasEnglish(word);

    /// <summary>
    /// Mark the single letters of a phrase as English and leave its words bare; adjacent letters share one marker pair
    /// ("washington d c" is marked once). G2P decides a word's language itself, from the dictionary and its neighbours.
    /// </summary>
    public static string EnMarked(string s)
    {
        var toks = RustText.SplitWhitespace(s);
        if (toks.Length == 0) return s;
        var need = toks.Select(t => RustText.CharCount(t) == 1).ToArray();
        if (!need.Any(b => b)) return string.Join(" ", toks);
        var output = new List<string>();
        var run = new List<string>();
        for (var i = 0; i < toks.Length; i++)
        {
            if (need[i])
            {
                run.Add(toks[i]);
            }
            else
            {
                if (run.Count > 0)
                {
                    output.Add("__start_en__" + string.Join(" ", run) + "__end_en__");
                    run.Clear();
                }
                output.Add(toks[i]);
            }
        }
        if (run.Count > 0) output.Add("__start_en__" + string.Join(" ", run) + "__end_en__");
        return string.Join(" ", output);
    }

    private static readonly string[] ViOnsets =
    [
        "ngh", "ch", "gh", "gi", "kh", "ng", "nh", "ph", "qu", "th", "tr",
        "b", "c", "d", "g", "h", "k", "l", "m", "n", "p", "r", "s", "t", "v", "x", "",
    ];

    private static readonly HashSet<string> ViRhymes = new(
    [
        "a", "ac", "ach", "ai", "am", "an", "ang", "anh", "ao", "ap", "at", "au", "ay",
        "e", "ec", "ech", "em", "en", "eng", "enh", "eo", "ep", "et", "eu",
        "i", "ia", "ich", "iec", "iem", "ien", "ieng", "iep", "iet", "ieu",
        "im", "in", "inh", "ip", "it", "iu",
        "o", "oa", "oac", "oach", "oai", "oan", "oang", "oanh", "oap", "oat", "oay",
        "oc", "oe", "oen", "oeo", "oi", "om", "on", "ong", "ooc", "oong", "op", "ot",
        "u", "ua", "uan", "uat", "uay", "uc", "ue", "uech", "uenh", "ui", "um", "un",
        "ung", "uo", "uoc", "uoi", "uom", "uon", "uong", "uot", "uou", "up", "ut",
        "uu", "uy", "uya", "uych", "uyen", "uyet", "uynh", "uyt", "uyu",
        "y", "yem", "yen", "yet", "yeu",
    ], StringComparer.Ordinal);

    /// <summary>File extensions keep their English-style reading after "chấm" ("chấm p y", "chấm jpg").</summary>
    private static readonly HashSet<string> FileExts = new(
    [
        "txt", "log", "tar", "gz", "zip", "rar", "sh", "py", "js", "ts", "cpp",
        "c", "h", "rs", "go", "java", "php", "json", "xml", "yaml", "yml", "md",
        "csv", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "exe", "dll",
        "so", "config", "ini", "bat", "jpg", "jpeg", "png", "gif", "bmp", "svg",
        "webp", "wav", "mp3", "mp4", "avi", "mkv", "html", "css", "sql", "db",
        "iso", "apk",
    ], StringComparer.Ordinal);

    private static bool IsViSyllable(string s)
    {
        foreach (var onset in ViOnsets)
            if (s.StartsWith(onset, StringComparison.Ordinal) && ViRhymes.Contains(s[onset.Length..])) return true;
        return false;
    }

    /// <summary>2 for a frequent syllable skeleton, 1 for a Vietnamese dictionary entry, 0 otherwise.</summary>
    private static uint SyllableViScore(PhonemeDictionary dict, string w)
    {
        if (WordLists.ViTopSyllables.Contains(w)) return 2;
        if (dict.LookupMerged(w) is { } p && !p.StartsWith("<en>", StringComparison.Ordinal)) return 1;
        if (dict.LookupCommon(w) != null) return 1;
        return 0;
    }

    private sealed class SplitPath
    {
        public uint JSegs;
        public uint JLetters;
        public uint Score;
        public List<byte> Lens = [];
        public List<(string Text, bool IsVi)> Parts = [];

        public SplitPath Clone() => new()
        {
            JSegs = JSegs, JLetters = JLetters, Score = Score, Lens = [.. Lens], Parts = [.. Parts],
        };
    }

    private static bool Better(SplitPath a, SplitPath b)
    {
        if (a.JSegs != b.JSegs) return a.JSegs < b.JSegs;
        if (a.JLetters != b.JLetters) return a.JLetters < b.JLetters;
        if (a.Lens.Count != b.Lens.Count) return a.Lens.Count < b.Lens.Count;
        if (a.Score != b.Score) return a.Score > b.Score;
        for (int i = a.Lens.Count - 1, j = b.Lens.Count - 1; i >= 0 && j >= 0; i--, j--)
            if (a.Lens[i] != b.Lens[j]) return a.Lens[i] > b.Lens[j];
        return false;
    }

    private static bool HasVowel(string s, string vowels)
    {
        foreach (var c in s)
            if (vowels.Contains(c)) return true;
        return false;
    }

    /// <summary>
    /// Split a lowercase ASCII run into toneless Vietnamese syllables and foreign pieces, preferring fewest foreign
    /// pieces, fewest foreign letters, fewest pieces, highest syllable score, then the longer final piece. Null when no
    /// Vietnamese syllable is found at all.
    /// </summary>
    private static List<(string Text, bool IsVi)>? SplitViSyllables(PhonemeDictionary dict, string s)
    {
        if (s.Length == 0 || !RustText.IsAscii(s)) return null;
        var n = s.Length;
        var dp = new SplitPath?[n + 1];
        dp[0] = new SplitPath();
        for (var i = 0; i < n; i++)
        {
            var b = dp[i];
            if (b == null) continue;
            for (var j = i + 1; j <= Math.Min(n, i + 7); j++)
            {
                var seg = s[i..j];
                if (!IsViSyllable(seg)) continue;
                var cand = b.Clone();
                var sc = SyllableViScore(dict, seg);
                if (b.Parts.Count > 0)
                {
                    var (prev, prevIsVi) = b.Parts[^1];
                    if (prevIsVi && WordLists.ViBigrams.Contains(prev + " " + seg)) sc += 3;
                }
                cand.Score += sc;
                cand.Lens.Add((byte)(j - i));
                cand.Parts.Add((seg, true));
                if (dp[j] == null || Better(cand, dp[j]!)) dp[j] = cand;
            }
            for (var j = i + 3; j <= n; j++)
            {
                var seg = s[i..j];
                if (!WordLists.EnTopWords.Contains(seg)) continue;
                var cand = b.Clone();
                cand.Lens.Add((byte)Math.Min(j - i, 255));
                cand.Parts.Add((seg, false));
                if (dp[j] == null || Better(cand, dp[j]!)) dp[j] = cand;
            }
            for (var j = i + 3; j <= n; j++)
            {
                var seg = s[i..j];
                if (HasVowel(seg, "aeiou")) continue;
                var cand = b.Clone();
                cand.JSegs += 1;
                cand.JLetters += (uint)(j - i);
                cand.Lens.Add((byte)Math.Min(j - i, 255));
                cand.Parts.Add((seg, false));
                if (dp[j] == null || Better(cand, dp[j]!)) dp[j] = cand;
            }
        }
        var best = dp[n];
        if (best == null || !best.Parts.Any(p => p.IsVi)) return null;
        return best.Parts;
    }

    internal static string ViLetterNames(string s)
    {
        var parts = new List<string>();
        foreach (var ch in RustText.Chars(s))
        {
            var cl = RustText.ToLower(ch);
            parts.Add(Resources.ViLetterNames.GetValueOrDefault(cl, cl));
        }
        return string.Join(" ", parts);
    }

    private static string RenderViSplit(List<(string Text, bool IsVi)> pieces) => string.Join(" ", pieces.Select(p =>
        p.IsVi ? p.Text : !HasVowel(p.Text, "aeiou") ? ViLetterNames(p.Text) : p.Text));

    private static string SpaceLetters(string lower) => string.Join(" ", RustText.Chars(lower));

    /// <summary>English reading of a letter cluster: short all-caps runs and clusters of two or fewer are spelled.</summary>
    private static string EnChunk(string t)
    {
        var val = RustText.ToLower(t);
        if ((RustText.All(t, RustText.IsUppercase) && RustText.Utf8Len(t) <= 4) || RustText.Utf8Len(t) <= 2)
            val = SpaceLetters(val);
        return EnMarked(val);
    }

    private static string NormLetterChunkEmail(PhonemeDictionary dict, string t, bool viCtx)
    {
        var lw = RustText.ToLower(t);
        if (!viCtx) return EnMarked(lw);
        if (RustText.CharCount(lw) == 1 || !HasVowel(lw, "aeiouy")) return ViLetterNames(lw);
        if (DictHas(dict, lw)) return lw;
        var pieces = SplitViSyllables(dict, lw);
        return pieces != null ? RenderViSplit(pieces) : lw;
    }

    private static string NormLetterChunk(PhonemeDictionary dict, string t, bool viCtx, bool afterDot)
    {
        if (!viCtx) return EnChunk(t);
        var lw = RustText.ToLower(t);
        if (afterDot && FileExts.Contains(lw))
            return HasVowel(lw, "aeiou") ? lw : ViLetterNames(lw);
        var len = RustText.Utf8Len(t);
        if (RustText.All(t, RustText.IsUppercase) && len <= 4 && len >= 2) return ViLetterNames(lw);
        if (RustText.CharCount(lw) == 1 || !HasVowel(lw, "aeiouy")) return ViLetterNames(lw);
        if (RustText.Any(t, RustText.IsUppercase) && RustText.Any(t, RustText.IsLowercase))
        {
            var pieces = new List<string>();
            var cur = "";
            foreach (var ch in RustText.Chars(t))
            {
                if (RustText.IsUppercase(RustText.FirstChar(ch)) && cur.Length > 0)
                {
                    pieces.Add(RustText.ToLower(cur));
                    cur = "";
                }
                cur += ch;
            }
            if (cur.Length > 0) pieces.Add(RustText.ToLower(cur));
            if (pieces.Count > 1 && pieces.All(IsViSyllable)) return string.Join(" ", pieces);
        }
        if (DictHas(dict, lw)) return lw;
        var split = SplitViSyllables(dict, lw);
        return split != null ? RenderViSplit(split) : lw;
    }

    [GeneratedRegex(@"([./:?&=/_ \-\\#@])", Rx.Options)]
    private static partial Regex ReTechSplit();

    [GeneratedRegex(@"([._\-+])", Rx.Options)]
    private static partial Regex ReEmailSplit();

    /// <summary>URLs, www hosts, bare domains, UNC and Windows paths, Unix paths, file names, dotted identifiers, IPv6.</summary>
    [GeneratedRegex(
        @"(?i)" + Rx.BL + @"(?:https?|ftp)://[\p{L}0-9.\-_~:/?#\[\]@!$&'()*+,;=]+" + Rx.B
        + @"|" + Rx.BL + @"(?:www\.)[\p{L}0-9.\-_~:/?#\[\]@!$&'()*+,;=]+" + Rx.B
        + @"|" + Rx.BL + @"[A-Za-z0-9.\-]+(?:\.com|\.vn|\.net|\.org|\.gov|\.edu|\.io|\.biz|\.info|\.dev|\.shop|\.app|\.tech|\.studio|\.online|\.store|\.ai|\.ly|\.me|\.gle|\.cc|\.co|\.tv|\.xyz|\.site|\.link|\.page|\.blog|\.news|\.pro)(?:[/?#][\p{L}0-9.\-_~:/?#\[\]@!$&'()*+,;=]*)?" + Rx.B
        + @"|(?<![" + Rx.WordClass + @"\\])\\\\[a-zA-Z0-9._\-]+(?:\\[\p{L}0-9._\-]+)*\\?"
        + @"|(?<![" + Rx.WordClass + @"\\])\\[a-zA-Z0-9._\-]+(?:\\[\p{L}0-9._\-]+)+\\?"
        + @"|(?<!" + Rx.W + @")/[a-zA-Z0-9._\-/]{2,}" + Rx.B
        + @"|" + Rx.BL + @"[a-zA-Z]:\\[a-zA-Z0-9._\\\-]+" + Rx.B
        + @"|" + Rx.BL + @"[a-zA-Z0-9._\-]+\.(?:txt|log|tar|gz|zip|sh|py|js|cpp|h|json|xml|yaml|yml|md|csv|pdf|docx|xlsx|exe|dll|so|config)" + Rx.BR
        + @"|" + Rx.BL + @"[a-zA-Z][a-zA-Z0-9]*(?:[._\-][a-zA-Z0-9]+){2,}" + Rx.BR
        + @"|" + Rx.BL + @"[a-fA-F0-9]{1,4}(?::[a-fA-F0-9]{1,4}){3,7}" + Rx.BR,
        Rx.Options)]
    internal static partial Regex ReTechnical();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}" + Rx.B, Rx.Options)]
    internal static partial Regex ReEmail();

    [GeneratedRegex(@"(?<![a-zA-Z\d,.])(\d+)/(\d+)(?![\d,.])", Rx.Options)]
    private static partial Regex ReSlashNumber();

    [GeneratedRegex(@"(?:=|\s)-((\d+)/(\d+))", Rx.Options)]
    private static partial Regex ReNegFrac();

    [GeneratedRegex(@"(?<![a-zA-Z\d,.])(\d+)/(\d+[a-zA-Z][a-zA-Z0-9]*)", Rx.Options)]
    private static partial Regex ReSlashAlnum();

    /// <summary>Split on a capture-group separator regex, keeping the separators: Rust's manual find_iter loop.</summary>
    private static List<string> SplitKeep(Regex re, string s)
    {
        var segments = new List<string>();
        var last = 0;
        foreach (Match m in re.Matches(s))
        {
            segments.Add(s[last..m.Index]);
            segments.Add(m.Value);
            last = m.Index + m.Length;
        }
        segments.Add(s[last..]);
        return segments;
    }

    private static string ReadOddChars(string s)
    {
        var res = new List<string>();
        foreach (var ch in RustText.Chars(RustText.ToLower(s)))
        {
            var cp = RustText.FirstChar(ch);
            if (RustText.IsAlphanumeric(cp))
            {
                if (ch.Length == 1 && RustText.IsAsciiDigit(ch[0])) res.Add(Num2Vi.N2WSingle(ch));
                else res.Add(Resources.ViLetterNames.GetValueOrDefault(ch, ch));
            }
            else
            {
                res.Add(ch);
            }
        }
        return string.Join(" ", res);
    }

    public static string NormalizeTechnical(PhonemeDictionary dict, string text, bool viCtx, bool enCtx)
    {
        var slashName = enCtx ? "slash" : viCtx ? "gạch chéo" : "gạch";
        var hyphenName = enCtx ? "dash" : viCtx ? "gạch nối" : "gạch ngang";
        var dotName = enCtx ? "dot" : "chấm";
        var underscoreName = enCtx ? "underscore" : "gạch dưới";
        var colonName = enCtx ? "colon" : "hai chấm";
        return ReTechnical().Replace(text, m =>
        {
            var orig = m.Value;
            var rest = orig;
            var res = new List<string>();

            var pIdx = RustText.ToLower(orig).IndexOf("://", StringComparison.Ordinal);
            if (pIdx >= 0)
            {
                var protocol = orig[..pIdx];
                if (viCtx)
                {
                    res.Add(ViLetterNames(RustText.ToLower(protocol)));
                    res.Add("hai chấm gạch chéo gạch chéo");
                }
                else
                {
                    var pLen = RustText.Utf8Len(protocol);
                    var pNorm = (RustText.All(protocol, RustText.IsUppercase) && pLen <= 4) || pLen <= 3
                        ? SpaceLetters(RustText.ToLower(protocol))
                        : RustText.ToLower(protocol);
                    res.Add(EnMarked(pNorm));
                    if (enCtx) res.Add("colon slash slash");
                }
                rest = orig[(pIdx + 3)..];
            }
            else if (orig.StartsWith('/'))
            {
                res.Add(slashName);
                rest = orig[1..];
            }

            var segments = SplitKeep(ReTechSplit(), rest);
            var idx = 0;
            var afterDot = false;
            while (idx < segments.Count)
            {
                var s = segments[idx];
                if (s.Length == 0) { idx++; continue; }
                var nextAfterDot = false;
                switch (s)
                {
                    case ".":
                    {
                        var nextSeg = "";
                        for (var j = idx + 1; j < segments.Count; j++)
                        {
                            var sj = segments[j];
                            if (sj.Length > 0 && !"./:?&=/_ -\\".Contains(sj, StringComparison.Ordinal))
                            {
                                nextSeg = sj;
                                break;
                            }
                        }
                        if (!enCtx && nextSeg.Length > 0 && Resources.DomainSuffixMap.TryGetValue(RustText.ToLower(nextSeg), out var suffix))
                        {
                            res.Add("chấm");
                            res.Add(suffix);
                            idx++;
                            var nextLower = RustText.ToLower(nextSeg);
                            while (idx < segments.Count && (segments[idx].Length == 0 || RustText.ToLower(segments[idx]) != nextLower))
                                idx++;
                            idx++;
                            continue;
                        }
                        res.Add(dotName);
                        nextAfterDot = true;
                        break;
                    }
                    case "/":
                    case "\\":
                        res.Add(slashName);
                        break;
                    case "-":
                        res.Add(hyphenName);
                        break;
                    case "_":
                        res.Add(underscoreName);
                        break;
                    case ":":
                        res.Add(colonName);
                        break;
                    case "?":
                        res.Add(enCtx ? "question mark" : "hỏi chấm");
                        break;
                    case "&":
                        res.Add(enCtx ? "and" : "và");
                        break;
                    case "=":
                        res.Add(enCtx ? "equals" : "bằng");
                        break;
                    case "#":
                        res.Add(enCtx ? "hash" : "thăng");
                        break;
                    case "@":
                        res.Add(enCtx ? "at" : "a còng");
                        break;
                    default:
                        if (RustText.Any(s, c => RustText.IsAlphabetic(c) && c >= 0x80))
                        {
                            res.Add(RustText.ToLower(s));
                        }
                        else if (!enCtx && Resources.DomainSuffixMap.TryGetValue(RustText.ToLower(s), out var sfx))
                        {
                            res.Add(sfx);
                        }
                        else if (RustText.All(s, c => c < 0x80 && RustText.IsAlphanumeric(c)))
                        {
                            string Digits(string d) => enCtx
                                ? Num2En.Digits(d)
                                : string.Join(" ", d.Select(c => Num2Vi.N2WSingle(c.ToString())));
                            if (s.All(RustText.IsAsciiDigit))
                            {
                                res.Add(Digits(s));
                            }
                            else
                            {
                                var subs = Num2En.ReSubTokens().Matches(s).Select(x => x.Value).ToList();
                                if (subs.Count > 1)
                                {
                                    foreach (var t in subs)
                                        res.Add(t.All(RustText.IsAsciiDigit) ? Digits(t) : NormLetterChunk(dict, t, viCtx, afterDot));
                                }
                                else
                                {
                                    res.Add(NormLetterChunk(dict, s, viCtx, afterDot));
                                }
                            }
                        }
                        else
                        {
                            res.Add(ReadOddChars(s));
                        }
                        break;
                }
                afterDot = nextAfterDot;
                idx++;
            }
            return string.Join(" ", res).Replace("  ", " ", StringComparison.Ordinal).Trim();
        });
    }

    public static string NormalizeEmails(PhonemeDictionary dict, string text, bool viCtx, bool enCtx)
    {
        var hyphenName = enCtx ? "dash" : viCtx ? "gạch nối" : "gạch ngang";
        var dotName = enCtx ? "dot" : "chấm";
        var atName = enCtx ? "at" : "a còng";
        return ReEmail().Replace(text, m =>
        {
            var email = m.Value;
            var parts = email.Split('@');
            if (parts.Length != 2) return email;

            string NormSegment(string s)
            {
                if (s.Length == 0) return "";
                if (s.All(RustText.IsAsciiDigit)) return enCtx ? Num2En.Words(s) : Num2Vi.N2W(s);
                if (RustText.All(s, c => c < 0x80 && RustText.IsAlphanumeric(c)))
                {
                    var subs = Num2En.ReSubTokens().Matches(s).Select(x => x.Value).ToList();
                    if (subs.Count > 1)
                    {
                        return string.Join(" ", subs.Select(t => t.All(RustText.IsAsciiDigit)
                            ? enCtx ? Num2En.Words(t) : Num2Vi.N2W(t)
                            : NormLetterChunkEmail(dict, t, viCtx)));
                    }
                    return NormLetterChunkEmail(dict, s, viCtx);
                }
                return ReadOddChars(s);
            }

            string ProcessPart(string p, bool isDomain)
            {
                var segments = SplitKeep(ReEmailSplit(), p);
                var res = new List<string>();
                var idx = 0;
                while (idx < segments.Count)
                {
                    var s = segments[idx];
                    if (s.Length == 0) { idx++; continue; }
                    switch (s)
                    {
                        case ".":
                            if (isDomain)
                            {
                                var nextSeg = "";
                                var peek = -1;
                                for (var j = idx + 1; j < segments.Count; j++)
                                {
                                    var sj = segments[j];
                                    if (sj.Length > 0 && !"._-+".Contains(sj, StringComparison.Ordinal))
                                    {
                                        nextSeg = sj;
                                        peek = j;
                                        break;
                                    }
                                }
                                if (!enCtx && nextSeg.Length > 0 && Resources.DomainSuffixMap.TryGetValue(RustText.ToLower(nextSeg), out var suffix))
                                {
                                    res.Add("chấm");
                                    res.Add(suffix);
                                    idx = peek + 1;
                                    continue;
                                }
                            }
                            res.Add(dotName);
                            break;
                        case "_":
                            res.Add(enCtx ? "underscore" : "gạch dưới");
                            break;
                        case "-":
                            res.Add(hyphenName);
                            break;
                        case "+":
                            res.Add(enCtx ? "plus" : "cộng");
                            break;
                        default:
                            res.Add(NormSegment(s));
                            break;
                    }
                    idx++;
                }
                return string.Join(" ", res);
            }

            var userNorm = ProcessPart(parts[0], false);
            var domainLower = RustText.ToLower(parts[1]);
            var domainNorm = !enCtx && Resources.CommonEmailDomains.TryGetValue(domainLower, out var dn)
                ? dn
                : ProcessPart(parts[1], true);
            return (userNorm + " " + atName + " " + domainNorm).Replace("  ", " ", StringComparison.Ordinal).Trim();
        });
    }

    public static string NormalizeSlashes(string text)
    {
        var res = ReNegFrac().Replace(text, m => (m.Value.StartsWith('=') ? "= âm " : " âm ") + m.V(1));

        var res2 = ReSlashAlnum().Replace(res, m =>
        {
            var n1 = m.V(1);
            var spoken = new List<string>();
            foreach (Match sub in Num2En.ReSubTokens().Matches(m.V(2)))
            {
                var t = sub.Value;
                if (t.All(RustText.IsAsciiDigit))
                {
                    spoken.Add(Num2Vi.N2W(t));
                }
                else
                {
                    spoken.Add(string.Join(" ", RustText.Chars(t).Select(ch =>
                    {
                        var cl = RustText.ToLower(ch);
                        return Resources.ViLetterNames.GetValueOrDefault(cl, cl);
                    })));
                }
            }
            return Num2Vi.N2W(n1) + " trên " + string.Join(" ", spoken);
        });

        return ReSlashNumber().Replace(res2, m => Num2Vi.N2W(m.V(1)) + " trên " + Num2Vi.N2W(m.V(2)));
    }
}
