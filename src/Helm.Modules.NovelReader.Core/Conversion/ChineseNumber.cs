namespace Helm.Modules.NovelReader.Conversion;

/// <summary>Chinese numerals ("一百零五", "两千", "十二") and chapter headings ("第十二章" → "Chương 12").</summary>
public static class ChineseNumber
{
    private const string Digits = "零〇一二两三四五六七八九十百千万";

    /// <summary>The value of a run of Chinese numerals or ASCII digits; false when it is neither.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        if (text.Length == 0) return false;
        if (char.IsAsciiDigit(text[0]))
        {
            foreach (var c in text)
            {
                if (!char.IsAsciiDigit(c) || value > 1_000_000_000) return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }
        long total = 0, section = 0, number = 0;
        foreach (var c in text)
        {
            var digit = DigitValue(c);
            if (digit >= 0)
            {
                number = digit;
                continue;
            }
            switch (c)
            {
                case '十': section += (number == 0 ? 1 : number) * 10; break;
                case '百': section += (number == 0 ? 1 : number) * 100; break;
                case '千': section += (number == 0 ? 1 : number) * 1000; break;
                case '万':
                    total += (section + number == 0 ? 1 : section + number) * 10_000;
                    section = 0;
                    break;
                default: return false;
            }
            number = 0;
        }
        value = total + section + number;
        return true;
    }

    /// <summary>
    /// A chapter heading at <paramref name="start"/>: 第, a number, then 章 节 回 卷 or 集 ("第1 章" with a space too).
    /// "第三回合" (third round) and "第一集团" are not headings.
    /// </summary>
    public static bool TryMatchHeading(string text, int start, out int length, out string translation)
    {
        length = 0;
        translation = "";
        if (text[start] != '第') return false;
        var i = start + 1;
        while (i < text.Length && text[i] == ' ') i++;
        var numberStart = i;
        while (i < text.Length && (char.IsAsciiDigit(text[i]) || Digits.Contains(text[i]))) i++;
        if (i == numberStart || !TryParse(text.AsSpan(numberStart, i - numberStart), out var number)) return false;
        while (i < text.Length && text[i] == ' ') i++;
        if (i >= text.Length) return false;
        var next = i + 1 < text.Length ? text[i + 1] : '\0';
        var unit = text[i] switch
        {
            '章' => "Chương",
            '节' => "Tiết",
            '回' when next != '合' => "Hồi",
            '卷' => "Quyển",
            '集' when next is not ('团' or '中' or '合') => "Tập",
            _ => null,
        };
        if (unit is null) return false;
        length = i + 1 - start;
        translation = unit + " " + number;
        return true;
    }

    private static int DigitValue(char c) => c switch
    {
        '零' or '〇' => 0,
        '一' => 1,
        '二' or '两' => 2,
        '三' => 3,
        '四' => 4,
        '五' => 5,
        '六' => 6,
        '七' => 7,
        '八' => 8,
        '九' => 9,
        _ => -1,
    };
}
