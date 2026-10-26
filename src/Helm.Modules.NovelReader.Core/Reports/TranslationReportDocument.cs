using System.Globalization;
using System.Text;
using Helm.Core.Sync;

namespace Helm.Modules.NovelReader.Reports;

/// <summary>
/// The reports as one Markdown document, to keep, share or hand to whoever improves the converter: each report with
/// its Chinese, what Helm showed, the right translation, the note and the pieces Helm cut it into. Given the converter
/// as it is now (null for a report it cannot convert, one of another novel), each report also says what Helm shows
/// today and whether that is the right translation.
/// </summary>
public static class TranslationReportDocument
{
    public static string Build(IReadOnlyList<SyncedItem<TranslationReport>> reports, DateTimeOffset now, Func<TranslationReport, string?>? convertNow = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Novel Reader: translation reports").AppendLine();
        var line = $"{reports.Count} {(reports.Count == 1 ? "report" : "reports")} · exported {Format(now)}";
        if (convertNow is not null)
        {
            var fixedCount = reports.Count(r => convertNow(r.Value) is { } current && IsFixed(r.Value, current));
            line += $" · {fixedCount} right now";
        }
        sb.AppendLine(line).AppendLine();
        var number = 0;
        foreach (var (_, report, _) in reports)
        {
            number++;
            var where = report.ChapterTitle.Length > 0 ? $"{report.BookTitle} · {report.ChapterTitle}" : report.BookTitle;
            sb.AppendLine($"## {number}. {where}").AppendLine();
            Field(sb, "Chinese", report.Chinese);
            Field(sb, "Helm", report.Helm);
            Field(sb, "Correct", report.Correct);
            if (convertNow?.Invoke(report) is { } current)
                Field(sb, "Helm now", current + (IsFixed(report, current) ? " ✅" : ""));
            Field(sb, "Note", report.Note);
            Field(sb, "Suggestion", report.Suggestion);
            Field(sb, "Suggestion note", report.SuggestionNote);
            Field(sb, "Error kind", report.ErrorKind);
            Field(sb, "Word", report.Word);
            if (report.Pieces.Count > 0)
                Field(sb, "Pieces", string.Join(" · ", report.Pieces.Select(p => $"{p.Chinese}={(p.Text.Length > 0 ? p.Text : "∅")} ({p.Kind})")));
            if (report.Paragraph.Length > 0 && report.Paragraph != report.Chinese) Field(sb, "Paragraph", report.Paragraph);
            sb.AppendLine($"- Reported {Format(report.CreatedAt)}{(report.Device.Length > 0 ? " on " + report.Device : "")}").AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Helm's translation is the right one, apart from capitals and spacing.</summary>
    public static bool IsFixed(TranslationReport report, string current) =>
        report.Correct.Length > 0 && Comparable(report.Correct) == Comparable(current);

    private static string Comparable(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLower(CultureInfo.InvariantCulture);

    private static void Field(StringBuilder sb, string name, string value)
    {
        if (value.Length == 0) return;
        // One line per field: a line break in a note would end the list item.
        sb.AppendLine($"- **{name}:** {value.ReplaceLineEndings(" ")}");
    }

    private static string Format(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
