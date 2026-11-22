using Helm.Core.Settings;

namespace Helm.Shell.BugReports;

/// <summary>This device's bug report options (settings/bug-reports.json; not synced).</summary>
public sealed class BugReportSettings : IVersionedSettings
{
    public const string StoreId = "bug-reports";

    public static int CurrentVersion => 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Phone: shaking it while Helm is open opens the report page.</summary>
    public bool ShakeToReport { get; set; } = true;

    public void Migrate(int fromVersion)
    {
    }
}
