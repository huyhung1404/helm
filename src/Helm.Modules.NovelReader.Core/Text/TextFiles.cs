using System.Text;

namespace Helm.Modules.NovelReader.Text;

/// <summary>
/// Reads the text files people download: novels and dictionaries come as UTF-8 (with or without a BOM), UTF-16 or
/// GB18030/GBK, the encoding Chinese sites still use. A BOM wins; then valid UTF-8; then GB18030.
/// </summary>
public static class TextFiles
{
    private static readonly Lazy<Encoding> Gb18030 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030");
    });

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 without a BOM, the encoding Novel Reader writes.</summary>
    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string ReadAllText(string path) => Decode(File.ReadAllBytes(path));

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken ct = default) =>
        Decode(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (LooksLikeUtf16(bytes, out var bigEndian))
            return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gb18030.Value.GetString(bytes);
        }
    }

    /// <summary>Writes through a temporary file so a crash never leaves half a file behind.</summary>
    public static void WriteAllTextAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, Utf8);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>UTF-16 without a BOM: in Chinese or Vietnamese text, every other byte of the first few is zero.</summary>
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, out bool bigEndian)
    {
        bigEndian = false;
        var sample = bytes[..(Math.Min(bytes.Length, 400) & ~1)];
        if (sample.Length < 8) return false;
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < sample.Length; i += 2)
        {
            if (sample[i] == 0) evenZeros++;
            if (sample[i + 1] == 0) oddZeros++;
        }
        var pairs = sample.Length / 2;
        if (oddZeros > pairs * 0.3 && evenZeros < pairs * 0.05) return true;
        if (evenZeros > pairs * 0.3 && oddZeros < pairs * 0.05)
        {
            bigEndian = true;
            return true;
        }
        return false;
    }
}
