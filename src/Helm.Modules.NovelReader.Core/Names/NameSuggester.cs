using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Names;

/// <summary>A word that is probably a character's name, how often it appears and how it would read.</summary>
public sealed record NameSuggestion(string Chinese, int Count, string Suggested);

/// <summary>
/// Finds likely character names in a novel: two to four Han characters that start with a common surname, appear at
/// least a few times and are not a dictionary phrase or a saved name already ("林宛" → "Lâm Uyển"). A two-character
/// candidate that nearly always continues with the same third character gives way to the longer one.
/// </summary>
public static class NameSuggester
{
    public const int MinCount = 3;

    /// <summary>Common surnames; compound ones are matched first.</summary>
    private static readonly string[] CompoundSurnames =
    [
        "欧阳", "司马", "上官", "诸葛", "东方", "皇甫", "慕容", "令狐", "南宫", "长孙", "宇文", "独孤", "公孙", "西门", "尉迟", "端木", "轩辕", "夏侯", "百里", "司徒",
    ];

    private const string Surnames =
        "王李张刘陈杨黄赵吴周徐孙马朱胡郭何高林罗郑梁谢宋唐许韩冯邓曹彭曾肖田董袁潘于蒋蔡余杜叶程苏魏吕丁任沈姚卢姜崔钟谭陆汪范金石廖贾夏韦付方白邹孟熊秦邱江尹薛闫段雷侯龙史陶黎贺顾毛郝龚邵万钱严覃武戴莫孔向汤常温康施文牛樊葛邢安齐易乔伍庞颜倪庄聂章鲁岳翟殷詹申欧耿关兰焦俞左柳甘祝包宁尚符舒阮柯纪梅童凌毕单季裴霍涂成苗谷盛曲翁冉骆蓝路游辛靳管柴蒙鲍华喻祁蒲房滕屈饶解牟艾尤阳时穆农司卓古吉缪简车项连芦麦褚娄窦戚岑景党宫费卜冷晏席卫米柏宗瞿桂全佟应臧闵苟邬边卞姬师和仇栾隋商刁沙荣巫寇桑郎甄丛仲虞敖巩明佘池查麻苑迟邝谢楚萧慕云墨洛夜";

    /// <summary>
    /// Counts the candidates in <paramref name="paragraphs"/>. <paramref name="isKnown"/> says whether a piece of Chinese
    /// is already a phrase or a name; <paramref name="hanViet"/> reads it.
    /// </summary>
    public static IReadOnlyList<NameSuggestion> Suggest(IEnumerable<string> paragraphs, Func<string, bool> isKnown,
        Func<string, string> hanViet, int max = 60)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var paragraph in paragraphs)
        {
            for (var i = 0; i < paragraph.Length; i++)
            {
                var surname = SurnameAt(paragraph, i);
                if (surname == 0) continue;
                for (var length = surname + 1; length <= Math.Min(surname + 2, 4); length++)
                {
                    if (i + length > paragraph.Length || !AllHan(paragraph, i, length)) break;
                    var word = paragraph.Substring(i, length);
                    counts[word] = counts.TryGetValue(word, out var n) ? n + 1 : 1;
                }
            }
        }

        // The count of the most frequent one-character continuation of each word.
        var longest = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (word, count) in counts)
        {
            if (word.Length < 3) continue;
            var stem = word[..^1];
            if (!longest.TryGetValue(stem, out var n) || count > n) longest[stem] = count;
        }

        var result = new List<NameSuggestion>();
        foreach (var (word, count) in counts)
        {
            if (count < MinCount || isKnown(word)) continue;
            // "林宛" beats "林宛撑"; but "林宛" gives way when it is nearly always "林宛儿".
            if (longest.TryGetValue(word, out var longer) && longer >= count * 0.8) continue;
            if (word.Length > 2 && counts.TryGetValue(word[..^1], out var shorter) && count < shorter * 0.8) continue;
            // A longer word whose tail is a common phrase ("林宛点头") is not a name.
            if (word.Length > 2 && isKnown(word[^2..])) continue;
            result.Add(new NameSuggestion(word, count, ChineseText.TitleCase(hanViet(word))));
        }
        return result.OrderByDescending(r => r.Count).ThenBy(r => r.Chinese, StringComparer.Ordinal).Take(max).ToList();
    }

    private static int SurnameAt(string text, int i)
    {
        if (i + 1 < text.Length)
        {
            foreach (var compound in CompoundSurnames)
                if (text[i] == compound[0] && text[i + 1] == compound[1]) return 2;
        }
        return Surnames.Contains(text[i]) ? 1 : 0;
    }

    private static bool AllHan(string text, int start, int length)
    {
        for (var i = start; i < start + length; i++)
            if (!ChineseText.IsHan(text[i])) return false;
        return true;
    }
}
