using Helm.Keyboard;

namespace Helm.Tests;

public sealed class KeyboardEngineTests
{
    private static string Type(string keys, TelexOptions? options = null) =>
        string.Join(' ', keys.Split(' ').Select(word => Telex.Transform(word, options ?? TelexOptions.Default)));

    [Theory]
    [InlineData("vieetj nam", "việt nam")]
    [InlineData("ddaij hocj", "đại học")]
    [InlineData("tieengs vieetj", "tiếng việt")]
    [InlineData("nguowif", "người")]
    [InlineData("nguoiwf", "người")]
    [InlineData("dduowcj", "được")]
    [InlineData("ruouwj", "rượu")]
    [InlineData("thuowr", "thuở")]
    [InlineData("muaw", "mưa")]
    [InlineData("cuwar", "cửa")]
    [InlineData("hoawcj", "hoặc")]
    [InlineData("quoocs", "quốc")]
    [InlineData("giuwax", "giữa")]
    [InlineData("gif", "gì")]
    [InlineData("giaf", "già")]
    [InlineData("gieengs", "giếng")]
    [InlineData("khuyeens", "khuyến")]
    [InlineData("chuyeenj", "chuyện")]
    [InlineData("nghieeng", "nghiêng")]
    [InlineData("uoongs", "uống")]
    [InlineData("yeeu", "yêu")]
    [InlineData("ngoaif", "ngoài")]
    [InlineData("khuyru", "khuỷu")]
    [InlineData("xuaan", "xuân")]
    [InlineData("tuaanf", "tuần")]
    [InlineData("hoanf", "hoàn")]
    [InlineData("toans", "toán")]
    [InlineData("huyts", "huýt")]
    [InlineData("caf phee", "cà phê")]
    [InlineData("ddi ddaau", "đi đâu")]
    [InlineData("tw", "tư")]
    [InlineData("w", "ư")]
    [InlineData("nhuw", "như")]
    public void Types_vietnamese_words(string keys, string expected) => Assert.Equal(expected, Type(keys));

    [Theory]
    [InlineData("vieetj", "việt")]
    [InlineData("vieejt", "việt")]
    [InlineData("vietej", "việt")]
    [InlineData("tieengs", "tiếng")]
    [InlineData("tieesng", "tiếng")]
    [InlineData("tiengse", "tiếng")]
    [InlineData("ddaij", "đại")]
    [InlineData("ddiaj", "địa")]
    [InlineData("nguoiwf", "người")]
    [InlineData("nguoifw", "người")]
    [InlineData("dodoir", "đổi")]
    [InlineData("dieemd", "điêm")]
    [InlineData("didf", "đì")]
    public void The_tone_and_marks_can_come_anywhere_after_the_vowel(string keys, string expected) =>
        Assert.Equal(expected, Type(keys));

    [Theory]
    [InlineData("hoaf", "hoà", "hòa")]
    [InlineData("thuys", "thuý", "thúy")]
    [InlineData("khoer", "khoẻ", "khỏe")]
    [InlineData("hoafn", "hoàn", "hoàn")]
    [InlineData("muaf", "mùa", "mùa")]
    public void Old_and_new_tone_style(string keys, string modern, string old)
    {
        Assert.Equal(modern, Type(keys));
        Assert.Equal(old, Type(keys, new TelexOptions { ModernToneStyle = false }));
    }

    [Theory]
    [InlineData("VIEETJ NAM", "VIỆT NAM")]
    [InlineData("Vieetj Nam", "Việt Nam")]
    [InlineData("DDaij", "Đại")]
    [InlineData("Dda", "Đa")]
    [InlineData("NGUOWIF", "NGƯỜI")]
    [InlineData("TW", "TƯ")]
    public void Keeps_upper_case(string keys, string expected) => Assert.Equal(expected, Type(keys));

    [Theory]
    [InlineData("ass", "as")]
    [InlineData("aaa", "aa")]
    [InlineData("ddd", "dd")]
    [InlineData("ww", "w")]
    [InlineData("uww", "uw")]
    [InlineData("asz", "a")]
    [InlineData("asf", "à")]
    public void A_key_typed_twice_takes_its_mark_off(string keys, string expected) => Assert.Equal(expected, Type(keys));

    [Theory]
    [InlineData("windows")]
    [InlineData("class")]
    [InlineData("cafe")]
    [InlineData("fix")]
    [InlineData("github")]
    [InlineData("book")]
    [InlineData("string")]
    [InlineData("for")]
    [InlineData("catf")]
    [InlineData("add")]
    public void Words_that_are_not_vietnamese_stay_as_typed(string keys) => Assert.Equal(keys, Type(keys));

    [Fact]
    public void Without_the_spell_check_every_telex_key_counts() =>
        Assert.Equal("clá", Type("clas", new TelexOptions { SpellCheck = false }));

    [Theory]
    [InlineData("cats", "cát")]
    [InlineData("hocj", "học")]
    [InlineData("catf", "catf")]
    [InlineData("hocr", "hocr")]
    public void Words_ending_in_c_ch_p_t_only_take_sac_or_nang(string keys, string expected) =>
        Assert.Equal(expected, Type(keys));

    [Fact]
    public void Each_key_shows_the_word_so_far()
    {
        var word = new TelexWord(TelexOptions.Default);
        var shown = new List<string>();
        foreach (var key in "ddaij")
        {
            word.Type(key);
            shown.Add(word.Text);
        }
        // What the Chrome address bar used to get wrong ("dđại"): the app only ever sees the whole word.
        Assert.Equal(["d", "đ", "đa", "đai", "đại"], shown);
    }

    [Fact]
    public void Backspace_takes_off_the_last_letter_and_typing_goes_on()
    {
        var word = TypeWord("vieetj");
        Assert.True(word.Backspace());
        Assert.Equal("việ", word.Text);
        word.Type('t');
        Assert.Equal("việt", word.Text);
    }

    [Fact]
    public void Backspace_then_a_new_tone()
    {
        var word = TypeWord("ddaij");
        word.Backspace();
        Assert.Equal("đạ", word.Text);
        word.Type('s');
        Assert.Equal("đá", word.Text);
    }

    [Fact]
    public void Backspace_on_a_word_that_is_not_vietnamese()
    {
        var word = TypeWord("windows");
        word.Backspace();
        Assert.Equal("window", word.Text);
        word.Type('s');
        Assert.Equal("windows", word.Text);
    }

    [Fact]
    public void Backspace_after_a_doubled_key()
    {
        var word = TypeWord("ass");
        word.Backspace();
        Assert.Equal("a", word.Text);
        word.Type('s');
        Assert.Equal("á", word.Text);
    }

    [Fact]
    public void Backspace_down_to_nothing_then_hands_backspace_to_the_app()
    {
        var word = TypeWord("ddi");
        Assert.True(word.Backspace());
        Assert.True(word.Backspace());
        Assert.True(word.IsEmpty);
        Assert.False(word.Backspace());
    }

    [Theory]
    [InlineData("việt")]
    [InlineData("người")]
    [InlineData("thuở")]
    [InlineData("được")]
    [InlineData("Đại")]
    [InlineData("khuỷu")]
    [InlineData("hoà")]
    [InlineData("quốc")]
    [InlineData("giữa")]
    public void Encode_gives_keys_that_type_the_word_back(string text) =>
        Assert.Equal(text, Telex.Transform(Telex.Encode(text)!, TelexOptions.Default));

    private static TelexWord TypeWord(string keys)
    {
        var word = new TelexWord(TelexOptions.Default);
        foreach (var key in keys) word.Type(key);
        return word;
    }
}
