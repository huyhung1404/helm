using System.Globalization;
using System.Text;

namespace Helm.Core.Text;

/// <summary>
/// Search that forgives accents and case, so "ghi chu" finds "Ghi chú" and "dien" finds "Điện". Used by the command
/// palette and the tools' own search boxes.
/// </summary>
public static class TextSearch
{
    /// <summary>Lower case without diacritics (Vietnamese đ becomes d). Keeps the length of most text the same.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c switch
            {
                'đ' or 'Đ' => 'd',
                _ => char.ToLowerInvariant(c),
            });
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>The folded words of a query, e.g. "  Ghi  chú " → ["ghi", "chu"].</summary>
    public static IReadOnlyList<string> Terms(string? query) =>
        Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// How well <paramref name="terms"/> match a title (and optionally a longer detail such as a note's text); 0 when
    /// some term matches neither. Every term must match. A term at the start of the title scores highest, then at the
    /// start of a word, then anywhere in the title, then in the detail. A single term can also match the initials of
    /// the title ("cp" → "Command Palette").
    /// </summary>
    public static double Score(IReadOnlyList<string> terms, string title, string? detail = null)
    {
        if (terms.Count == 0) return 0;
        var t = Fold(title);
        string? d = null;
        double total = 0;
        foreach (var term in terms)
        {
            double best;
            var index = t.IndexOf(term, StringComparison.Ordinal);
            if (index == 0) best = 1.0;
            else if (index > 0 && IsWordStart(t, index)) best = 0.8;
            else if (index > 0) best = 0.6;
            else
            {
                d ??= Fold(detail);
                if (d.Contains(term, StringComparison.Ordinal)) best = 0.25;
                else if (terms.Count == 1 && term.Length >= 2 && Initials(t).StartsWith(term, StringComparison.Ordinal)) best = 0.5;
                else return 0;
            }
            total += best;
        }
        // Shorter titles win ties: "Notes" beats "Notes settings" for "notes".
        return total / terms.Count + 0.05 / (1 + t.Length / 10.0);
    }

    private static bool IsWordStart(string text, int index) => !char.IsLetterOrDigit(text[index - 1]);

    private static string Initials(string folded)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < folded.Length; i++)
            if (char.IsLetterOrDigit(folded[i]) && (i == 0 || !char.IsLetterOrDigit(folded[i - 1]))) builder.Append(folded[i]);
        return builder.ToString();
    }
}
