using System.Globalization;
using System.Text.RegularExpressions;

namespace Helm.Modules.Wallet;

/// <summary>A transaction read from a bank's notification, before it is saved.</summary>
/// <param name="Amount">Signed: negative is money out.</param>
public sealed record ParsedTransaction(
    string Bank,
    string Account,
    decimal Amount,
    decimal? Balance,
    string Description,
    DateTimeOffset OccurredAt,
    string Original)
{
    /// <summary>
    /// What the same transaction read twice has in common (the app's notification and the SMS, or a notification the
    /// phone shows again): the bank, the amount and the balance after it; without a balance, the description and the
    /// minute.
    /// </summary>
    public string Fingerprint => Balance is { } balance
        ? string.Create(CultureInfo.InvariantCulture, $"{Bank}|{Amount:0.##}|{balance:0.##}")
        : string.Create(CultureInfo.InvariantCulture, $"{Bank}|{Amount:0.##}|{string.Join(' ', WalletText.Words(Description).Order(StringComparer.Ordinal))}|{OccurredAt.UtcDateTime:yyyyMMddHHmm}");
}

/// <summary>
/// Reads a balance-change notification of a Vietnamese bank (app or SMS, with or without accents): the signed amount,
/// the balance after it, the account, the description and the time. Works on the folded text (see
/// <see cref="WalletText.Fold"/>) and takes the description from the original at the same place. Returns null for
/// anything that is not clearly a transaction (an OTP, an advert), so nothing wrong is saved.
/// </summary>
public static partial class BankNotificationParser
{
    private const string Number = @"\d{1,3}(?:[.,]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?";

    // "+5,000,000", "-150.000 VND", "GD:-150,000": a sign right before the number, not inside a word or a reference
    // ("123-456"), and not a percentage.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])([+-])\s?(?:vnd\s?)?(" + Number + @")(?!\d)(?!\s?%)", RegexOptions.CultureInvariant)]
    private static partial Regex SignedAmount();

    // "So tien: 150,000", "PS: 150,000" (an amount without a sign; the direction comes from other words), or "ghi no
    // 150,000" / "ghi co 150,000" (the direction is in the keyword).
    [GeneratedRegex(@"(?<![\p{L}])(ghi no|ghi co|so tien(?: giao dich| gd)?|phat sinh|ps|amount|giao dich)\s*[:=]?\s*(?:vnd\s?)?(" + Number + @")(?!\d)(?!\s?%)", RegexOptions.CultureInvariant)]
    private static partial Regex KeywordAmount();

    [GeneratedRegex(@"(?<!bien dong )(?<![\p{L}])(?:so du(?: kha dung| cuoi(?: ky)?| hien tai| moi| tk)?|sd(?: moi)?|balance|avail(?:able)?(?: balance)?|bal)\s*[:=]?\s*(?:la\s+)?(?:vnd\s?)?([+-]?)\s?(" + Number + @")(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Balance();

    [GeneratedRegex(@"(?<![\p{L}])(?:tai khoan(?: thanh toan)?|stk|tk|account|acct|a/c)\s*(?:so\s*)?[:.]?\s*([0-9x*]{2,}[0-9x*.\-]*[0-9x*]|[0-9x*]{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex Account();

    [GeneratedRegex(@"(?<![\d:])([01]?\d|2[0-3])[:h]([0-5]\d)(?::([0-5]\d))?(?![\d:])", RegexOptions.CultureInvariant)]
    private static partial Regex Time();

    [GeneratedRegex(@"(?<![\d/])(\d{1,2})[/-](\d{1,2})[/-](\d{4}|\d{2})(?![\d/])", RegexOptions.CultureInvariant)]
    private static partial Regex Date();

    [GeneratedRegex(@"(?<![\p{L}])(?<!tien )(?:noi dung(?: giao dich| gd| ck| chuyen khoan)?|nd|mo ta|dien giai|description|content|memo|ghi chu|gd)\s*[:.]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex DescriptionStart();

    // Where a description stops: the next field.
    [GeneratedRegex(@"(?<![\p{L}])(?:so du|sd|tai khoan|stk|tk|so tien|luc|thoi gian|ngay giao dich|balance)(?![\p{L}])\s*[:.]?\s*[\d+-]", RegexOptions.CultureInvariant)]
    private static partial Regex NextField();

    [GeneratedRegex(@"(?<![\p{L}])(?:ghi co|credit|tang|nhan duoc|da nhan|bao co)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex CreditWords();

    [GeneratedRegex(@"(?<![\p{L}])(?:ghi no|debit|giam|tru|bao no|da thanh toan|da chi)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex DebitWords();

    [GeneratedRegex(@"^\d{3,}\s*-\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingReference();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    // A one-time code and the word for it close together, in either order: "Ma OTP cua Quy khach la 123456",
    // "SmartOTP: 556677", "123456 la ma xac thuc". A warning without a code ("khong cung cap OTP cho ai") is not one,
    // and neither is a date, an amount or a long account number.
    private const string CodeWord = @"(?:otp|(?<![\p{L}])(?:ma xac thuc|ma xac nhan|ma kich hoat|ma bao mat|mat khau|ma pin|passcode|password|verification code|security code))(?![\p{L}])";
    private const string Code = @"(?<![\d.,/+-])\d{4,8}(?!\d|[.,/]\d)";

    [GeneratedRegex(CodeWord + @"(?:[^.!?\n]|(?<=\d)[.,](?=\d)){0,40}?" + Code + "|" + Code + @"[^.!?\n]{0,20}?" + CodeWord, RegexOptions.CultureInvariant)]
    private static partial Regex OneTimeCode();

    /// <summary>
    /// Whether the notification carries a one-time code (OTP, verification code, password). Helm never keeps those,
    /// not even as a notification it could not read.
    /// </summary>
    public static bool HasOneTimeCode(string? title, string? text) =>
        OneTimeCode().IsMatch(WalletText.Fold(WalletText.Clean(title + "\n" + text)));

    /// <summary>Reads one notification; null when it is not a transaction Helm can read with confidence.</summary>
    /// <param name="bank">The bank's name as stored on the transaction.</param>
    /// <param name="title">The notification's title (an amount in it is read too).</param>
    /// <param name="text">The notification's text (the big text when there is one).</param>
    /// <param name="postedAt">When the phone showed it: the time of the transaction when the text has none.</param>
    /// <param name="zone">The phone's time zone, for the time written in the text.</param>
    public static ParsedTransaction? Parse(string bank, string? title, string? text, DateTimeOffset postedAt, TimeZoneInfo zone)
    {
        var body = WalletText.Clean(text);
        var head = WalletText.Clean(title);
        // A title such as "+5,000,000 VND" carries the amount; "Biến động số dư" or "ACB" carries nothing.
        var original = head.Any(char.IsAsciiDigit) && !body.Contains(head, StringComparison.Ordinal) ? head + "\n" + body : body;
        if (original.Length == 0) return null;
        var folded = WalletText.Fold(original);
        var taken = new List<(int Start, int End)>();

        decimal? balance = null;
        var balanceMatch = Balance().Match(folded);
        if (balanceMatch.Success && WalletText.TryParseNumber(balanceMatch.Groups[2].Value, out var b))
        {
            balance = balanceMatch.Groups[1].Value == "-" ? -b : b;
            taken.Add((balanceMatch.Index, balanceMatch.Index + balanceMatch.Length));
        }

        var amount = SignedAmountOf(folded, taken) ?? KeywordAmountOf(folded, taken);
        if (amount is not { } value || value == 0) return null;

        var account = "";
        var accountMatch = Account().Match(folded);
        if (accountMatch.Success && accountMatch.Groups[1].Value.Count(char.IsAsciiDigit) >= 3)
        {
            var g = accountMatch.Groups[1];
            account = original.Substring(g.Index, g.Length);
            taken.Add((accountMatch.Index, accountMatch.Index + accountMatch.Length));
        }

        // Without a balance or an account it could be an advert with a price in it.
        if (balance is null && account.Length == 0) return null;

        var occurred = TimeOf(folded, postedAt, zone, taken);
        var description = DescriptionOf(original, folded, taken);
        return new ParsedTransaction(bank, account, value, balance, description, occurred, original);
    }

    private static decimal? SignedAmountOf(string folded, List<(int Start, int End)> taken)
    {
        Match? best = null;
        var bestScore = int.MinValue;
        foreach (Match m in SignedAmount().Matches(folded))
        {
            if (Overlaps(taken, m.Index, m.Index + m.Length)) continue;
            var score = 0;
            var after = folded[(m.Index + m.Length)..].TrimStart();
            if (after.StartsWith("vnd", StringComparison.Ordinal) || after.StartsWith('d') && (after.Length == 1 || !char.IsLetter(after[1])) || after.StartsWith('₫')) score += 2;
            var before = folded[Math.Max(0, m.Index - 24)..m.Index];
            if (before.Contains("so tien", StringComparison.Ordinal) || before.Contains("gd", StringComparison.Ordinal) || before.Contains("ps", StringComparison.Ordinal)
                || before.Contains("giao dich", StringComparison.Ordinal) || before.Contains("tk", StringComparison.Ordinal) || before.Contains("tai khoan", StringComparison.Ordinal)) score += 2;
            if (WalletText.TryParseNumber(m.Groups[2].Value, out var v) && v >= 1000) score += 1;
            if (score > bestScore)
            {
                best = m;
                bestScore = score;
            }
        }
        if (best is null || !WalletText.TryParseNumber(best.Groups[2].Value, out var amount)) return null;
        taken.Add((best.Index, best.Index + best.Length));
        return best.Groups[1].Value == "-" ? -amount : amount;
    }

    private static decimal? KeywordAmountOf(string folded, List<(int Start, int End)> taken)
    {
        foreach (Match m in KeywordAmount().Matches(folded))
        {
            if (Overlaps(taken, m.Index, m.Index + m.Length) || !WalletText.TryParseNumber(m.Groups[2].Value, out var amount)) continue;
            var keyword = m.Groups[1].Value;
            var credit = keyword == "ghi co" || keyword != "ghi no" && CreditWords().IsMatch(folded);
            var debit = keyword == "ghi no" || keyword != "ghi co" && DebitWords().IsMatch(folded);
            if (credit == debit) return null; // no direction, or both: not sure enough
            taken.Add((m.Index, m.Index + m.Length));
            return credit ? amount : -amount;
        }
        return null;
    }

    private static DateTimeOffset TimeOf(string folded, DateTimeOffset postedAt, TimeZoneInfo zone, List<(int Start, int End)> taken)
    {
        var posted = TimeZoneInfo.ConvertTime(postedAt, zone);
        DateTime? day = null;
        TimeSpan? time = null;
        foreach (Match m in Date().Matches(folded))
        {
            if (Overlaps(taken, m.Index, m.Index + m.Length)) continue;
            var d = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var mo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var y = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (y < 100) y += 2000;
            if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), mo)) continue;
            day = new DateTime(y, mo, d);
            taken.Add((m.Index, m.Index + m.Length));
            break;
        }
        foreach (Match m in Time().Matches(folded))
        {
            if (Overlaps(taken, m.Index, m.Index + m.Length)) continue;
            time = new TimeSpan(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
            taken.Add((m.Index, m.Index + m.Length));
            break;
        }
        if (day is null && time is null) return postedAt;
        var local = (day ?? posted.Date) + (time ?? (day == posted.Date ? posted.TimeOfDay : TimeSpan.FromHours(12)));
        // Only the time: a time later than the notification was yesterday's.
        if (day is null && local > posted.DateTime.AddMinutes(5)) local = local.AddDays(-1);
        DateTimeOffset result;
        try
        {
            result = new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        catch (ArgumentException)
        {
            return postedAt;
        }
        // A time far from the notification is a misread (another date in the text): keep the notification's.
        return result < postedAt.AddDays(-7) || result > postedAt.AddHours(1) ? postedAt : result;
    }

    private static string DescriptionOf(string original, string folded, List<(int Start, int End)> taken)
    {
        foreach (Match m in DescriptionStart().Matches(folded))
        {
            var start = m.Index + m.Length;
            if (Overlaps(taken, m.Index, start)) continue;
            var next = NextField().Match(folded, start);
            var end = next.Success ? next.Index : folded.Length;
            var text = Tidy(original[start..end]);
            if (text.Count(char.IsLetter) >= 2) return text;
        }
        // No label (e.g. "TK 1903xxx So tien GD:-150,000 So du:1,234,567 THANH TOAN QR"): what follows the last field.
        var after = taken.Count == 0 ? 0 : taken.Max(t => t.End);
        var rest = after < original.Length ? original[after..] : "";
        var nextField = NextField().Match(WalletText.Fold(rest));
        if (nextField.Success) rest = rest[..nextField.Index];
        rest = Tidy(rest.TrimStart(' ', '.', ',', ';', ':', '-', ')', '\n'));
        if (WalletText.Fold(rest).StartsWith("vnd", StringComparison.Ordinal)) rest = Tidy(rest[3..].TrimStart(' ', '.', ',', ';', ':', ')'));
        return rest.Count(char.IsLetter) >= 3 ? rest : "";
    }

    private static string Tidy(string text)
    {
        var s = Spaces().Replace(text, " ").Trim().TrimEnd('.', ',', ';', ':', '-').Trim();
        s = LeadingReference().Replace(s, "");
        return s.Length <= 300 ? s : s[..300].TrimEnd();
    }

    private static bool Overlaps(List<(int Start, int End)> taken, int start, int end) => taken.Any(t => start < t.End && t.Start < end);
}
