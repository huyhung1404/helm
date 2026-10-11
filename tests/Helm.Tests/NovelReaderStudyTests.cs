using Helm.Modules.NovelReader;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;

namespace Helm.Tests;

/// <summary>Study mode's readings: pinyin from mozillazg's lists, Hán Việt, names capitalised.</summary>
public sealed class NovelReaderStudyTests
{
    /// <summary>The two list formats: per character (the most common reading first) and per word.</summary>
    internal const string PinyinFile =
        "# pinyin-data\nU+94F6: yín  # 银\nU+884C: xíng,háng  # 行\nU+957F: cháng,zhǎng  # 长\nU+5927: dà,dài  # 大\nU+6797: lín  # 林\nU+5B9B: wǎn  # 宛\n" +
        "U+6CB9: yóu  # 油\nU+7EB8: zhǐ  # 纸\nU+4F1E: sǎn  # 伞\nU+7AE0: zhāng  # 章\nU+7B2C: dì  # 第\n" +
        "# phrase-pinyin-data\n银行: yín háng\n长大: zhǎng dà\n";

    [Fact]
    public void Words_read_by_their_phrase_first_and_the_rest_character_by_character()
    {
        var pinyin = PinyinDictionary.Parse(PinyinFile);

        // 行 and 长 have several readings: the phrase list settles them.
        Assert.Equal("yín háng", pinyin.Of("银行"));
        Assert.Equal("zhǎng dà", pinyin.Of("长大"));
        Assert.Equal("xíng", pinyin.Of("行"));
        Assert.Equal("yóu zhǐ sǎn", pinyin.Of("油纸伞"));
        Assert.Equal("dì 1 zhāng", pinyin.Of("第1章"));
        // A character no list has is left out.
        Assert.Equal("yóu", pinyin.Of("油某"));
        Assert.Equal("Lín Wǎn", PinyinDictionary.Capitalize("lín wǎn"));
        Assert.Equal(13, pinyin.Count);
    }

    [Fact]
    public void Pinyin_files_are_dictionaries_of_their_own_and_load_with_the_others()
    {
        Assert.Equal(DictionaryKind.Pinyin, DictionarySet.KindOf("Pinyin.txt"));
        Assert.Equal(DictionaryKind.Pinyin, DictionarySet.KindOf("PinyinPhrases.txt"));
        var folder = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Pinyin.txt"), PinyinFile);
            Assert.Equal("yín háng", DictionarySet.Load(folder).Pinyin.Of("银行"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // A user whose saved sources predate study mode still gets the pinyin addresses.
        var settings = new NovelReaderSettings { DictionarySources = new(StringComparer.OrdinalIgnoreCase) { ["VietPhrase.txt"] = "https://example.org/vp.txt" } };
        Assert.Equal(["Pinyin.txt", "PinyinPhrases.txt"], settings.PinyinSources().Keys.Order());
    }

    [Fact]
    public void Each_word_gets_its_chinese_pinyin_and_han_viet_and_names_read_like_names()
    {
        // 林宛撑伞。 → Lâm Uyển / ô / .
        var line = new ConvertedLine("林宛油纸伞。",
        [
            new Segment(0, 2, "Lâm Uyển", SegmentKind.Name, ""),
            new Segment(2, 3, "ô giấy dầu", SegmentKind.Phrase, " "),
            new Segment(5, 1, ".", SegmentKind.Text, ""),
        ]);
        var hanViet = new Dictionary<string, string> { ["林宛"] = "lâm uyển", ["油纸伞"] = "du chỉ tán" };

        var readings = WordReading.Of(line, PinyinDictionary.Parse(PinyinFile), c => hanViet[c]);

        Assert.Equal(new WordReading("林宛", "Lín Wǎn", "Lâm Uyển"), readings[0]);
        Assert.Equal(new WordReading("油纸伞", "yóu zhǐ sǎn", "du chỉ tán"), readings[1]);
        Assert.Null(readings[2]);
    }
}
