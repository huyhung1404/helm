using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Modules.WeeklyReport;

/// <summary>A report the user finished.</summary>
public sealed record SavedReport(DateOnly From, DateOnly To, string Text, DateTimeOffset Saved)
{
    public string Label => $"{From:dd/MM} – {To:dd/MM/yyyy}";
}

/// <summary>The finished reports, on this PC only, encrypted for this Windows user (they describe the user's work).</summary>
public sealed class ReportHistory(HelmPaths paths, ISecretProtector protector)
{
    public const int Keep = 30;
    private const string Purpose = "helm.weekly-report.history";

    private string FilePath => Path.Combine(paths.ModuleDataDirectory(WeeklyReportModule.ModuleId), "reports.bin");

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SavedReport> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var bytes = protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
            try
            {
                return JsonSerializer.Deserialize<List<SavedReport>>(bytes) ?? [];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Keeps <paramref name="report"/> in place of an earlier one of the same week.</summary>
    public IReadOnlyList<SavedReport> Save(SavedReport report)
    {
        var list = Load().Where(r => r.From != report.From).Prepend(report).Take(Keep).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(list);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, protector.Protect(bytes, Purpose));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        return list;
    }
}
