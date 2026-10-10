using System.Globalization;
using System.Text;

namespace Helm.Modules.NovelReader.Text;

/// <summary>Character classes and the clean-up applied to Chinese text before it is looked up.</summary>
public static class ChineseText
{
    /// <summary>A Han character (CJK unified ideographs, extension A, compatibility ideographs) or 〇.</summary>
    public static bool IsHan(char c) =>
        c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿' or '〇';

    public static bool HasHan(string text)
    {
        foreach (var c in text)
            if (IsHan(c)) return true;
        return false;
    }

    /// <summary>
    /// Puts one line in the form the dictionaries are written in, like QuickTranslator does: Chinese punctuation
    /// becomes ASCII punctuation followed by a space where a space belongs (VietPhrase keys contain ", " and ": "),
    /// full-width letters and digits become narrow, and runs of spaces collapse. Quotes keep their direction (“ ”),
    /// which the output needs for spacing. The result is what the converter's positions refer to.
    /// </summary>
    public static string NormalizeLine(string line)
    {
        var sb = new StringBuilder(line.Length + 8);
        foreach (var c in line)
        {
            switch (c)
            {
                case '，': case '、': sb.Append(", "); break;
                case '。': case '．': sb.Append('.'); break;
                case '：': sb.Append(": "); break;
                case '；': sb.Append("; "); break;
                case '？': sb.Append('?'); break;
                case '！': sb.Append('!'); break;
                case '…': sb.Append("..."); break;
                case '「': case '『': case '“': sb.Append('“'); break;
                case '」': case '』': case '”': sb.Append('”'); break;
                case '‘': sb.Append('‘'); break;
                case '’': sb.Append('’'); break;
                case '【': sb.Append('['); break;
                case '】': sb.Append(']'); break;
                case '～': sb.Append('~'); break;
                case '　': case '\t': case ' ': sb.Append(' '); break;
                case '﻿': case '​': case '\0': break;
                case >= '！' and <= '～': sb.Append((char)(c - 0xFEE0)); break;
                default: sb.Append(c); break;
            }
        }
        // Collapse spaces; drop a space before closing punctuation and at both ends.
        var collapsed = new StringBuilder(sb.Length);
        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];
            if (c == ' ')
            {
                if (collapsed.Length == 0 || collapsed[^1] is ' ' or '“' or '‘' or '(' or '[') continue;
                var next = i + 1 < sb.Length ? sb[i + 1] : '\0';
                if (next is '\0' or ',' or '.' or '!' or '?' or ':' or ';' or ')' or ']' or '”' or '’') continue;
            }
            collapsed.Append(c);
        }
        while (collapsed.Length > 0 && collapsed[^1] == ' ') collapsed.Length--;
        // "……" and "......" are one ellipsis.
        var result = collapsed.ToString();
        while (result.Contains("....", StringComparison.Ordinal)) result = result.Replace("....", "...", StringComparison.Ordinal);
        return result;
    }

    /// <summary>Normalizes a dictionary key the same way as text, so keys written with Chinese punctuation still match.</summary>
    public static string NormalizeKey(string key)
    {
        foreach (var c in key)
        {
            if (c is '，' or '、' or '。' or '：' or '；' or '？' or '！' or '…' or '　' or '「' or '」' or '『' or '』' or '﻿'
                or (>= '！' and <= '～'))
                return NormalizeLine(key);
        }
        return key.Trim();
    }

    /// <summary>The meanings of a dictionary value ("a/b|c"), trimmed, empty ones dropped unless all are empty.</summary>
    public static IReadOnlyList<string> Meanings(string value)
    {
        var parts = value.Split('/', '|');
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0 && !list.Contains(t)) list.Add(t);
        }
        return list;
    }

    /// <summary>The first meaning of a dictionary value; empty when the entry deliberately translates to nothing.</summary>
    public static string FirstMeaning(string value)
    {
        var end = value.IndexOfAny(['/', '|']);
        return (end < 0 ? value : value[..end]).Trim();
    }

    /// <summary>"trương tam phong" → "Trương Tam Phong" (each word starts with a capital letter).</summary>
    public static string TitleCase(string text)
    {
        var chars = text.ToCharArray();
        var start = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i]) || chars[i] is '-' or '·')
            {
                start = true;
                continue;
            }
            if (start && char.IsLetter(chars[i])) chars[i] = char.ToUpper(chars[i], CultureInfo.InvariantCulture);
            start = false;
        }
        return new string(chars);
    }

    /// <summary>Upper-cases the first letter, leaving the rest as written.</summary>
    public static string Capitalize(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i])) continue;
            if (char.IsUpper(text[i])) return text;
            return string.Concat(text.AsSpan(0, i), char.ToUpper(text[i], CultureInfo.InvariantCulture).ToString(), text.AsSpan(i + 1));
        }
        return text;
    }
}
