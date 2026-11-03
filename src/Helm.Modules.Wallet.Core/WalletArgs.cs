using System.Globalization;
using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Modules.Wallet;

/// <summary>Reading the Wallet tools' arguments: amounts as people say them, local days and times.</summary>
internal static class WalletArgs
{
    /// <summary>A positive amount in đồng: a number, or text like 45000, 45.000, 45k, 1tr2; null when left out.</summary>
    public static decimal? Amount(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var raw) || raw.ValueKind == JsonValueKind.Null) return null;
        var text = raw.ValueKind == JsonValueKind.Number ? raw.GetDecimal().ToString(CultureInfo.InvariantCulture) : McpArgs.String(args, name) ?? "";
        if (text.Trim().Length == 0) return null;
        return WalletText.TryParseAmount(text.Trim().TrimStart('+', '-'), out var value) && value > 0
            ? value
            : throw new McpToolException($"{text} is not an amount. Try 45000, 45.000, 45k or 1tr2.");
    }

    public static decimal RequiredAmount(JsonElement args, string name) =>
        Amount(args, name) ?? throw new McpToolException($"{name} is required.");

    /// <summary>"2026-10-01" (noon that day, so it stays on that day in any zone) or "2026-10-01T12:30" (that local time).</summary>
    public static DateTimeOffset When(string text, TimeZoneInfo zone)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), zone.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0))));
        if (!DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new McpToolException($"{text} is not a day or a time like 2026-10-01 or 2026-10-01T12:30.");
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The id in a record an Undo gets.</summary>
    public static string Id(JsonElement record, string name = "id") =>
        record.TryGetProperty(name, out var id) && id.GetString() is { Length: > 0 } text ? text : throw new McpToolException($"The result has no {name}.");
}

/// <summary>Amounts and days as Ask Helm's card shows them: "300.000đ", "28/10".</summary>
public static class WalletSpeech
{
    /// <summary>Dots between the thousands, whatever the device's language.</summary>
    public static string Money(decimal amount) => Math.Abs(amount).ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.') + "đ";

    public static string Day(DateOnly day) => day.ToString("dd/MM", CultureInfo.InvariantCulture);
}
