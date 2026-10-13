using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Helm.App.Android;

/// <summary>
/// Chinese text drawn with the phone's own Chinese font, named outright. Avalonia otherwise asks the system for a font
/// for each character it cannot draw, and on some phones that answer goes wrong after a while (every Hán character
/// became a box in Novel Reader). The font is the one the system uses for simplified Chinese (from its font list),
/// read from /system/fonts the first time a Chinese character is drawn.
/// </summary>
public static class ChineseFont
{
    private const string FamilyName = "Helm Chinese";
    private static readonly Uri s_key = new("fonts:HelmChinese");

    /// <summary>CJK punctuation, ideographs (with the extensions) and full-width forms.</summary>
    private const string Ranges = "U+2E80-2FDF, U+3000-303F, U+3400-4DBF, U+4E00-9FFF, U+F900-FAFF, U+FE30-FE4F, U+FF00-FFEF, U+20000-2FA1F";

    /// <summary>Makes Chinese characters use the phone's Chinese font before anything else (Inter stays the default).</summary>
    public static AppBuilder WithChineseFont(this AppBuilder builder) => builder
        .With(new FontManagerOptions
        {
            // WithInterFont sets these options too; setting them again replaces them, so Inter is named here as well.
            DefaultFamilyName = "fonts:Inter#Inter",
            FontFallbacks = [new FontFallback { FontFamily = new FontFamily($"{s_key}#{FamilyName}"), UnicodeRange = UnicodeRange.Parse(Ranges) }],
        })
        .AfterSetup(_ => FontManager.Current.AddFontCollection(new SystemChineseFontCollection(s_key, FamilyName)));

    /// <summary>
    /// The file and face of the system's simplified Chinese font: from the font lists (lang="zh-Hans", else another Han
    /// language), else a few well-known files. Null when the phone has none.
    /// </summary>
    /// <summary>The system's font lists, newest format first.</summary>
    internal static string[] FontLists { get; set; } = ["/system/etc/font_fallback.xml", "/system/etc/fonts.xml", "/product/etc/fonts_customization.xml"];

    internal static string[] FontFolders { get; set; } = ["/system/fonts", "/product/fonts", "/system/product/fonts"];

    internal static (string Path, int Index)? Find(IEnumerable<string>? lists = null, IEnumerable<string>? folders = null)
    {
        lists ??= FontLists;
        string[] dirs = [.. folders ?? FontFolders];
        string? Resolve(string name) => dirs.Select(d => Path.Combine(d, name.Trim())).FirstOrDefault(File.Exists);

        foreach (var language in new[] { "zh-Hans", "zh-Hant", "ja" })
        {
            foreach (var list in lists)
            {
                if (!File.Exists(list)) continue;
                XDocument document;
                try
                {
                    document = XDocument.Load(list);
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var family in document.Descendants("family"))
                {
                    var languages = ((string?)family.Attribute("lang") ?? "").Split(',', ' ');
                    if (!languages.Contains(language, StringComparer.OrdinalIgnoreCase)) continue;
                    // The sans-serif face; a serif one is only for text that asks for serif.
                    foreach (var font in family.Elements("font").Where(f => f.Attribute("fallbackFor") is null))
                    {
                        var name = font.Nodes().OfType<XText>().Select(t => t.Value.Trim()).FirstOrDefault(t => t.Length > 0);
                        if (name is null || Resolve(name) is not { } path) continue;
                        var index = int.TryParse((string?)font.Attribute("index"), NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : 0;
                        return (path, index);
                    }
                }
            }
        }
        foreach (var name in new[] { "NotoSansCJK-Regular.ttc", "NotoSansSC-Regular.otf", "NotoSansHans-Regular.otf", "DroidSansFallbackFull.ttf", "DroidSansFallback.ttf" })
        {
            // In Noto Sans CJK the simplified Chinese face is the third (Japanese, Korean, SC, TC, HK).
            if (Resolve(name) is { } path) return (path, name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ? 2 : 0);
        }
        return null;
    }

    /// <summary>
    /// One face of a font file as a font of its own: a .ttc collection's face <paramref name="index"/> is copied out
    /// (its table directory and tables), since a font loaded from a stream is always the first face. Other files are read as they are.
    /// </summary>
    internal static byte[] ReadFace(string path, int index)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (ReadTag(reader) != "ttcf")
        {
            file.Position = 0;
            var whole = new byte[file.Length];
            file.ReadExactly(whole);
            return whole;
        }
        ReadUInt32(reader); // version
        var count = (int)ReadUInt32(reader);
        if (index < 0 || index >= count) index = 0;
        file.Position = 12 + 4L * index;
        file.Position = ReadUInt32(reader);
        var sfntVersion = ReadUInt32(reader);
        var numTables = ReadUInt16(reader);
        var searchRange = ReadUInt16(reader);
        var entrySelector = ReadUInt16(reader);
        var rangeShift = ReadUInt16(reader);
        var tables = new List<(uint Tag, uint Checksum, uint Offset, uint Length)>();
        for (var t = 0; t < numTables; t++)
            tables.Add((ReadUInt32(reader), ReadUInt32(reader), ReadUInt32(reader), ReadUInt32(reader)));

        var headerSize = 12 + 16 * numTables;
        var size = (long)headerSize + tables.Sum(t => (t.Length + 3L) & ~3L);
        var output = new byte[size];
        var span = output.AsSpan();
        WriteUInt32(span, 0, sfntVersion);
        WriteUInt16(span, 4, numTables);
        WriteUInt16(span, 6, searchRange);
        WriteUInt16(span, 8, entrySelector);
        WriteUInt16(span, 10, rangeShift);
        var at = headerSize;
        for (var t = 0; t < tables.Count; t++)
        {
            var (tag, checksum, offset, length) = tables[t];
            var record = 12 + 16 * t;
            WriteUInt32(span, record, tag);
            WriteUInt32(span, record + 4, checksum);
            WriteUInt32(span, record + 8, (uint)at);
            WriteUInt32(span, record + 12, length);
            file.Position = offset;
            file.ReadExactly(output, at, (int)length);
            at += (int)((length + 3) & ~3u);
        }
        return output;
    }

    private static string ReadTag(BinaryReader reader) => new(reader.ReadChars(4));

    private static uint ReadUInt32(BinaryReader reader) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));

    private static ushort ReadUInt16(BinaryReader reader) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));

    private static void WriteUInt32(Span<byte> span, int at, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span[at..], value);

    private static void WriteUInt16(Span<byte> span, int at, ushort value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(span[at..], value);

    /// <summary>The system's Chinese font under one known name, loaded the first time Avalonia asks for it.</summary>
    private sealed class SystemChineseFontCollection(Uri key, string alias) : FontCollectionBase
    {
        private readonly object _gate = new();
        private bool _loaded;

        public override Uri Key { get; } = key;

        public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        {
            Load();
            return base.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
        }

        public override bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch, string? familyName,
            CultureInfo? culture, out Typeface typeface)
        {
            Load();
            return base.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out typeface);
        }

        private void Load()
        {
            lock (_gate)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (Find() is not { } font) return;
                    using var stream = new MemoryStream(ReadFace(font.Path, font.Index), writable: false);
                    if (TryAddGlyphTypeface(stream, out var typeface))
                        TryAddGlyphTypeface(alias, new FontCollectionKey(typeface.Style, typeface.Weight, typeface.Stretch), typeface);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
                {
                    // No readable Chinese font: Avalonia's own lookup still runs after this one.
                    global::Android.Util.Log.Warn("Helm", "Could not load the system Chinese font: " + ex.Message);
                }
            }
        }
    }
}
