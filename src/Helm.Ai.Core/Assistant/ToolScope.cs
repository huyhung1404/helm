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
            "an ", "uong", "ca phe", "cafe", "xang", "vi ", "wallet", "spend", "money", "owe", "pho", "com ", "bun"]),
        ("tracker", ["nhac", "viec", "todo", "task", "lam ", "deadline", "can ", "phai", "han ", "xong", "danh sach", "list",
            "chia nho", "buoc nho", "cac buoc"]),
        ("notes", ["ghi chu", "note", "luu ", "y tuong", "ghi lai", "ghi nho"]),
        ("habit", ["thoi quen", "habit", "tap ", "chay bo", "doc sach", "streak", "diem danh", "thien"]),
        ("mission", ["muc tieu", "mission", "ke hoach", "buoc", "lo trinh", "aptis", "on thi"]),
        ("vocab", ["tu vung", "tu moi", "word", "vocab", "nghia", "tieng anh", "english"]),
        ("phone", ["bao thuc", "danh thuc", "goi dau", "goi day", " day", "hen gio", "timer", "lich", "calendar", "su kien", "hop ",
            "copy", "clipboard", "dan ", "sao chep"]),
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
        if (Amount().IsMatch(plain)) found.Add("wallet");
        return found;
    }

    /// <summary>
    /// The tools for this turn: those of the groups the conversation has needed so far, <paramref name="scope"/>, grown
    /// by what <paramref name="text"/> asks for; all of them when nothing is known yet.
    /// </summary>
    public static IReadOnlyList<AssistantTool> Pick(IReadOnlyList<AssistantTool> all, ISet<string> scope, string text)
    {
        var asked = GroupsFor(text);
        scope.UnionWith(asked.Count > 0 || scope.Count > 0 ? asked : all.Select(t => GroupOf(t.Name)));
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

    [GeneratedRegex(@"\d[\d.,]*\s*(k|tr|d|nghin|ngan|trieu|dong|vnd)\b")]
    private static partial Regex Amount();
}
