using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Helm.Ai.Assistant;

/// <summary>
/// Which of Ask Helm's tools a request needs, so a call carries only those (each tool's schema costs tokens on every
/// call, on the API and on an agent alike). Tools come in groups by their name ("wallet_…", "phone_…"); a request
/// that names none of the groups' words gets them all, so nothing is ever out of reach.
/// </summary>
public static partial class ToolScope
{
    /// <summary>The groups and the words (without diacritics, lower case) that call for them.</summary>
    private static readonly (string Group, string[] Words)[] Groups =
    [
        ("wallet", ["tien", "nghin", "ngan", "trieu", "chi ", "tieu", "mua", "tra ", "no ", "muon", "vay", "luong", "thu nhap", "nhan ",
            "an ", "uong", "ca phe", "cafe", "xang", "vi ", "wallet", "spend", "money", "owe", "pho", "com ", "bun", "chia"]),
        ("bill", ["hoa don", "tien dien", "tien nuoc", "tien nha", "tien mang", "internet", "wifi", "dong tien", "dinh ky", "hang thang", "bill"]),
        ("tracker", ["nhac", "viec", "todo", "task", "lam ", "deadline", "can ", "phai", "han ", "xong", "danh sach", "list",
            "chia nho", "buoc nho", "cac buoc"]),
        ("notes", ["ghi chu", "note", "luu ", "y tuong", "ghi lai", "ghi nho",
            "ngay quan trong", "sinh nhat", "ngay gio", "dam gio", "dang kiem", "bao hiem", "ten mien", "gia han"]),
        ("habit", ["thoi quen", "habit", "tap ", "chay bo", "doc sach", "streak", "diem danh", "thien", "gym"]),
        ("mission", ["muc tieu", "mission", "ke hoach", "buoc", "lo trinh", "aptis", "on thi", "ky thi", "ielts", "toeic", "chung chi"]),
        ("vocab", ["tu vung", "tu moi", "word", "vocab", "nghia", "tieng anh", "english"]),
        ("ai", ["ban tin", "tong ket", "tom tat", "brief", "huan luyen", "tu dong", "don scratch", "dat tieu de", "hoan tac tieu de"]),
        ("phone", ["bao thuc", "danh thuc", "goi dau", "goi day", " day", "hen gio", "timer", "lich", "calendar", "su kien", "hop ",
            "copy", "clipboard", "dan ", "sao chep", "duong toi", "duong den", "duong di", "chi duong", "dan duong", "ban do", "map",
            "mo ", "open", "grab", "youtube", "zalo", "chia se", "share", "nhan tin", "tin nhan", "soan tin"]),
        ("workspace", ["claude", "workspace", "phien", "session"]),
    ];

    /// <summary>The group of a tool: its name's first word, singular ("habits_list" and "habit_check" are "habit").</summary>
    public static string GroupOf(string toolName)
    {
        var head = toolName.Split('_', 2)[0];
        return head.EndsWith('s') && head is not "notes" ? head[..^1] : head;
    }

    /// <summary>The groups <paramref name="text"/> asks for; empty when it names none of them.</summary>
    public static IReadOnlySet<string> GroupsFor(string text)
    {
        var plain = " " + Plain(text) + " ";
        // A dictated note ("ghi chú: …") is a note whatever its words say: they are its content, not the request.
        if (plain.TrimStart().StartsWith("ghi chu", StringComparison.Ordinal) || plain.TrimStart().StartsWith("note ", StringComparison.Ordinal)
            || plain.TrimStart().StartsWith("note:", StringComparison.Ordinal))
            return new HashSet<string>(StringComparer.Ordinal) { "notes" };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (group, words) in Groups)
            if (words.Any(w => plain.Contains(w, StringComparison.Ordinal))) found.Add(group);
        // An amount ("45k", "200 nghìn", "1tr5", "30.000đ") is money.
        var amount = Amount().IsMatch(plain);
        if (amount) found.Add("wallet");
        // Eating or drinking without an amount ("uống đủ nước rồi", "ăn sáng rồi") is as likely a habit as a spending.
        if (!amount && FoodWords.Any(w => plain.Contains(w, StringComparison.Ordinal))) found.Add("habit");
        // "Nhắc Nam trả 200k": a message to someone, shared from the phone.
        if (RemindSomeone().IsMatch(plain)) found.UnionWith(["wallet", "phone"]);
        return found;
    }

    /// <summary>
    /// The tools for this turn: those of the groups the conversation has needed so far, <paramref name="scope"/>, grown
    /// by what <paramref name="text"/> asks for; all of them when it names no group (at the start, or a later turn such
    /// as "anh đi gym rồi" after "phở 45k": the old groups alone might not reach what it needs).
    /// </summary>
    /// <param name="known">The scope already says what this turn is about (the first turn of a card opened about a
    /// transaction): words naming no group add nothing.</param>
    public static IReadOnlyList<AssistantTool> Pick(IReadOnlyList<AssistantTool> all, ISet<string> scope, string text, bool known = false)
    {
        var asked = GroupsFor(text);
        scope.UnionWith(asked.Count > 0 || known ? asked : all.Select(t => GroupOf(t.Name)));
        return [.. all.Where(t => scope.Contains(GroupOf(t.Name)))];
    }

    private static string Plain(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        return plain.ToString();
    }

    /// <summary>Eating and drinking words of the wallet group, which name habits too.</summary>
    private static readonly string[] FoodWords = [" an ", "uong", "ca phe", "cafe", "pho", "com ", "bun"];

    // No \b after the unit: in "1tr2" a digit follows "tr".
    [GeneratedRegex(@"\d[\d.,]*\s*(k|tr|d|nghin|ngan|trieu|dong|vnd)(?![a-z])")]
    private static partial Regex Amount();

    [GeneratedRegex(@"\b(nhac|doi)\s+(?!anh\b|em\b|toi\b|minh\b)\S+(\s+\S+)?\s+(tra|no)\b")]
    private static partial Regex RemindSomeone();
}
