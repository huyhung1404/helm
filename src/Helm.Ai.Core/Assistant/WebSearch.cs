using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Helm.Core.Mcp;

namespace Helm.Ai.Assistant;

/// <summary>
/// Ask Helm on the web (A8 weather, rates, gold, news; M4 checking a doubtful post): which requests need it, what the
/// turn tells the model, and the tool the model calls when it finds it needs the web itself. Web turns run on the API
/// only (Claude's server-side web search; agents on a PC have their web tools off on purpose), and only while the
/// user turned <see cref="AiFeatureIds.WebSearch"/> on: each search costs <see cref="AiPrices.WebSearchUsd"/>.
/// </summary>
public static partial class WebSearch
{
    /// <summary>The most searches one turn may make (Claude's <c>max_uses</c>).</summary>
    public const int MaxUses = 3;

    /// <summary>The tool a model calls when a request it got turns out to need the web (<see cref="LookupTool"/>).</summary>
    public const string LookupName = "web_lookup";

    /// <summary>Said when a post is to be checked while web search is off.</summary>
    public const string Off = "Checking a post needs Web search, which is off: turn it on in AI → Features on the PC or General › AI features on the phone (about $0.01 a search, on the API).";

    /// <summary>What a request to check a post starts with ("Helm: Kiểm tra tin" sends it that way).</summary>
    public const string FactCheckPrefix = "Kiểm tra tin này: ";

    /// <summary>Words (without diacritics, lower case) of a request that needs the web now.</summary>
    private static readonly string[] Words =
    [
        "thoi tiet", "du bao", "nhiet do", "troi mua", "troi co mua", "weather",
        "ty gia", "ti gia", "gia usd", "gia do", "gia euro", "exchange rate",
        "gia vang", "vang sjc", "vang nhan", "gold price",
        "gia xang", "gia bitcoin", "gia btc", "chung khoan", "vn-index", "vnindex", "co phieu",
        "tin tuc", "tin moi", "thoi su", "news", "ket qua tran", "ty so",
        "tim tren mang", "tim tren web", "tra tren mang", "tra google", "search web", "tim web", "len mang",
    ];

    /// <summary>"Kiểm tra tin…", "tin này có thật không", "fake news": a post to check against the web.</summary>
    private static readonly string[] CheckWords =
    [
        "kiem tra tin", "kiem chung", "check tin", "fact check", "fact-check", "tin nay co that", "co that khong",
        "tin gia", "tin don", "fake news", "lua dao khong",
    ];

    /// <summary>Whether <paramref name="text"/> asks about something only the web knows now (weather, rates, gold, news).</summary>
    public static bool Asks(string text)
    {
        var plain = " " + Plain(text) + " ";
        return IsFactCheck(text) || Words.Any(w => plain.Contains(w, StringComparison.Ordinal)) || TodayAt().IsMatch(plain);
    }

    /// <summary>Whether <paramref name="text"/> asks to check a post or a claim (M4).</summary>
    public static bool IsFactCheck(string text)
    {
        var plain = " " + Plain(text) + " ";
        return CheckWords.Any(w => plain.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>The request "Helm: Kiểm tra tin" sends for a selected or shared post.</summary>
    public static string FactCheckRequest(string post) => FactCheckPrefix + post.Trim();

    /// <summary>The user's words for a web turn, with what this turn may do on the web (kept out of the cached prefix).</summary>
    internal static string Turn(string text, bool factCheck) =>
        (factCheck ? FactCheckNote : WebNote) + "\n\n" + text;

    /// <summary>
    /// The tool a model offered no web calls when the request needs current facts (the switch is on, its words did not
    /// say so): <paramref name="turnOn"/> adds web search to the chat for the rest of the conversation.
    /// </summary>
    internal static AssistantTool LookupTool(Func<bool> turnOn) => new(new McpTool(LookupName,
        "Turn on web search for this request when the answer needs current facts from the internet (weather, prices, "
        + "exchange rates, news, checking whether a post is true). Each search costs the user money: call it only then, "
        + "never for Helm's own data.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["why"] = new JsonObject { ["type"] = "string", ["description"] = "What needs the web, in a few words." },
            },
        },
        (_, _) => Task.FromResult<object?>(turnOn()
            ? "Web search is on for this conversation: search now (at most a few searches), then answer and name the sites."
            : "Web search cannot be used here: answer from what you know and say it may be out of date.")) { ReadOnly = true },
        AssistantToolMode.Read);

    private const string WebNote = """
        [Web search is on for this request: search only for what needs current facts (at most a few searches). Answer in
        Vietnamese, short, and end with the sources by site name, e.g. "Nguồn: VnExpress, SJC".]
        """;

    private const string FactCheckNote = """
        [Check this post with web search: find what reliable sources (official sites, major newspapers, fact-checkers) say
        about its main claim. Answer in Vietnamese in two or three short sentences: say first how trustworthy it is (Đúng,
        Sai, Gây hiểu lầm or Chưa kiểm chứng được), then why, and end with the sources by site name, e.g. "Nguồn:
        VnExpress, Bộ Y tế". Never call it true only because many pages repeat it.]
        """;

    private static string Plain(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        return plain.ToString();
    }

    /// <summary>"Hôm nay ở Đà Lạt thế nào", "ngày mai ở Huế có mưa": the weather somewhere.</summary>
    [GeneratedRegex(@"\b(hom nay|ngay mai|cuoi tuan)\s+o\s+.*\b(the nao|ra sao|mua|nang|nong|lanh|bao nhieu do)\b")]
    private static partial Regex TodayAt();

    internal static string Hosts(IEnumerable<string> urls) => string.Join(", ", urls.Select(Host).Where(h => h.Length > 0).Distinct().Take(3));

    internal static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? (uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host) : "";

    internal static IEnumerable<string> Urls(JsonElement citations) =>
        citations.ValueKind != JsonValueKind.Array ? []
        : citations.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            .Select(c => c.GetProperty("url").GetString()!);
}
