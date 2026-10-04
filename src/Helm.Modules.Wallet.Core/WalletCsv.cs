using System.Globalization;
using System.Text;

namespace Helm.Modules.Wallet;

/// <summary>Every transaction as CSV, for a spreadsheet (both apps save or share it).</summary>
public static class WalletCsv
{
    public static string Build(WalletStore store, TimeZoneInfo zone)
    {
        var categories = store.Categories(includeHidden: true).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("Date,Time,Amount,Category,Description,Bank,Account,Balance,Note,Source");
        foreach (var item in store.Transactions())
        {
            var t = item.Value;
            var local = TimeZoneInfo.ConvertTime(t.OccurredAt, zone);
            var category = t.CategoryId is { } id && categories.TryGetValue(id, out var c) ? c.Name : "";
            sb.Append(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(local.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(',')
              .Append(t.Amount.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(Field(category)).Append(',')
              .Append(Field(t.Description)).Append(',')
              .Append(Field(t.Bank)).Append(',')
              .Append(Field(t.Account)).Append(',')
              .Append(t.Balance?.ToString("0.##", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(Field(t.Note)).Append(',')
              .Append(t.Source == TransactionSource.Notification ? "notification" : "manual")
              .AppendLine();
        }
        return sb.ToString();
    }

    private static string Field(string value)
    {
        // Spreadsheets run text that starts with = + - @ as a formula.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
