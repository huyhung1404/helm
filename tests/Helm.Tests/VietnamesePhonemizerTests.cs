using System.Text;
using Helm.Speech.Vietnamese;
using Helm.Speech.Vietnamese.Normalization;
using Xunit.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The C# port of sea-g2p 0.10.0 (Vietnamese) against the original: expected.tsv holds input and the wheel's
/// <c>SEAPipeline("vi").run(input, punc_norm=True)</c> for a few thousand lines (sea-g2p's own test strings, numbers,
/// dates, units, abbreviations, code-switching, URLs, punctuation and novel prose), fuzz.tsv random token soup. The
/// corpus tests need the 62 MB dictionary, which CI does not have, so they return early without it.
/// </summary>
public sealed class VietnamesePhonemizerTests(ITestOutputHelper output)
{
    private const string DefaultDictionary = @"C:\tmp\vieneu-src\sea-g2p\python\sea_g2p\sea_g2p.bin";

    [Theory]
    [InlineData("Xin chào", "Xin chào.")]
    [InlineData("Xin chào!", "Xin chào.")]
    [InlineData("Vâng...", "Vâng.")]
    [InlineData("Ừ…", "Ừ.")]
    [InlineData("Đi thôi?", "Đi thôi.")]
    [InlineData("3. Mở đầu và kết thúc của câu chuyện này", "3, Mở đầu và kết thúc của câu chuyện này.")]
    [InlineData("Hắn đứng dậy rồi đi ra khỏi phòng", "Hắn đứng dậy rồi đi ra khỏi phòng.")]
    [InlineData("Hắn đứng dậy rồi đi ra khỏi phòng!", "Hắn đứng dậy rồi đi ra khỏi phòng!")]
    [InlineData("Một, hai, ba, bốn, năm, sáu", "Một, hai, ba, bốn, năm, sáu.")]
    [InlineData("A. B. C. Đây là một câu khá dài nhé", "A, B, C, Đây là một câu khá dài nhé.")]
    [InlineData("U.S.A. là nước Mỹ rộng lớn và giàu có", "U.S.A, là nước Mỹ rộng lớn và giàu có.")]
    [InlineData("!!!", ".")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void PunctuationNorm_matches_sea_g2p(string input, string expected) =>
        Assert.Equal(expected, VietnamesePhonemizer.PunctuationNorm(input));

    [Theory]
    [InlineData("21", "hai mươi mốt")]
    [InlineData("15", "mười lăm")]
    [InlineData("105", "một trăm lẻ năm")]
    [InlineData("1001", "một nghìn không trăm lẻ một")]
    [InlineData("05", "không năm")]
    [InlineData("1000000000000", "một nghìn tỷ")]
    [InlineData("1000000000000000000", "một tỷ tỷ")]
    public void Cardinals_read_the_Vietnamese_way(string number, string expected) =>
        Assert.Equal(expected, Num2Vi.N2W(number));

    [Fact]
    public void Collapsed_punctuation_keeps_the_strongest_mark() =>
        Assert.Equal("quo vadis, domine? lạy chúa", PunctuationRules.CollapsePunctRuns("quo vadis, domine? , lạy chúa"));

    [Fact]
    public void Corpus_matches_sea_g2p() => RunCorpus("expected.tsv");

    [Fact]
    public void Fuzz_corpus_matches_sea_g2p() => RunCorpus("fuzz.tsv");

    [Fact]
    public void Parallel_calls_give_the_same_phonemes()
    {
        using var phonemizer = OpenOrSkip();
        if (phonemizer == null) return;
        var rows = ReadRows("expected.tsv").Take(600).ToList();
        var failures = 0;
        Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = 8 }, row =>
        {
            if (phonemizer.Phonemize(row.Input) != row.Expected) Interlocked.Increment(ref failures);
        });
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Open_rejects_a_file_that_is_not_a_dictionary()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[64]);
            Assert.Throws<InvalidDataException>(() => VietnamesePhonemizer.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private void RunCorpus(string name)
    {
        using var phonemizer = OpenOrSkip();
        if (phonemizer == null) return;
        var rows = ReadRows(name);
        var failures = new List<string>();
        foreach (var (input, expected) in rows)
        {
            var got = phonemizer.Phonemize(input);
            if (got != expected) failures.Add($"IN : {input}\nEXP: {expected}\nGOT: {got}");
        }
        output.WriteLine($"{name}: {rows.Count - failures.Count}/{rows.Count} match");
        foreach (var f in failures.Take(20)) output.WriteLine(f);
        Assert.Empty(failures);
    }

    private VietnamesePhonemizer? OpenOrSkip()
    {
        var path = Environment.GetEnvironmentVariable("HELM_SEA_G2P_DICT");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = DefaultDictionary;
        if (!File.Exists(path))
        {
            output.WriteLine("sea_g2p.bin not found (set HELM_SEA_G2P_DICT); corpus test skipped.");
            return null;
        }
        return VietnamesePhonemizer.Open(path);
    }

    private static List<(string Input, string Expected)> ReadRows(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sea-g2p", name);
        var rows = new List<(string, string)>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            rows.Add((Unescape(line[..tab]), Unescape(line[(tab + 1)..])));
        }
        return rows;
    }

    // The fixture escapes backslash, tab, newline and carriage return as \\ \t \n \r.
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n switch { 'n' => "\n", 't' => "\t", 'r' => "\r", '\\' => "\\", _ => "\\" + n });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}
