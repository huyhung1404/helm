namespace Helm.Modules.NovelReader.Scraping;

/// <summary>
/// Fanqie's web reader shows its 362 most common characters through a font of its own: the page holds code points
/// U+E3E8–U+E55B (private use) that the font draws as 的, 是, 在… This turns them back into the characters. The table is
/// from fysh1010/mcp-server-fanqie (src/fontDecrypt.ts, MIT, see Resources/fanqie-chars.LICENSE.txt), checked against
/// the live site on 2026-10-25 (font dc027189e0ba4cd): seven of its entries were wrong (行 意, 占 点, 木 本, 绘 给, 多 名, 己 情 and
/// 情 己, letters that look alike) and are corrected here, found by lining ten chapters of 十日终焉 up with the same chapters
/// from ixdzs8 (13,000 scrambled characters, each corrected one agreed by 21 to 70 of them). A page with another font
/// is not decoded with it (wrong text).
/// </summary>
public static class FanqieText
{
    /// <summary>The id in the address of the font the table was made for.</summary>
    public const string FontId = "dc027189e0ba4cd";

    private const int First = 0xE3E8;

    /// <summary>The character of each code point from <see cref="First"/>; \0 where the font has none.</summary>
    private const string Table = "D在主特家军然表场4要只v和\06别还g现儿岁\0\0此象月3出战工相o男直失世F都平文什VO将真T那当\0会立些u是十张学气大爱两命全后东性通被1它乐接而感车山公了常以何可话先pi叫轻M士w着变尔快l个说少色里安花远7难师放t报认面道S\0克地度I好机U民写把万同水新没书电吃像斯5为y白几日教看但第加候作上拉住有法r事应位利你声身国问马女他Y比父xAHNsX边美对所金活回意到z从j知又内因点Q三定8Rb正或夫向德听更\0得告并本q过记L让打f人就者去原满体做经K走如孩cG给使物\0最笑部\0员等受k行一条果动光门头见往自解成处天能于名其发总母的死手入路进心来h时力多开已许d至由很界n小与Z想代么分生口再妈望次西风种带J\0实情才这\0E我神格长觉间年眼无不亲关结0友信下却重己老2音字m呢明之前高PB目太e9起稜她也W用方子英每理便四数期中C外样a海们任";

    public static bool IsFanqie(Uri uri) => uri.Host.EndsWith("fanqienovel.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The text holds Fanqie's private characters.</summary>
    public static bool IsScrambled(string text) => text.Any(c => c >= First && c < First + Table.Length);

    /// <summary>The text with Fanqie's private characters turned back into the real ones.</summary>
    public static string Decode(string text)
    {
        if (!IsScrambled(text)) return text;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var at = chars[i] - First;
            if (at >= 0 && at < Table.Length && Table[at] != '\0') chars[i] = Table[at];
        }
        return new string(chars);
    }

    /// <summary>The page draws its text with the font the table was made for (its style sheets name it).</summary>
    public static bool KnowsFont(string styles) => styles.Contains(FontId, StringComparison.Ordinal);

    internal static int Length => Table.Length;

    internal static int Gaps => Table.Count(c => c == '\0');
}
