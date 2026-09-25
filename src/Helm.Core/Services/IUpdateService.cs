namespace Helm.Core.Services;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
    NotInstalled,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? Version = null,
    string? ReleaseNotes = null,
    long SizeBytes = 0,
    string? Message = null)
{
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate);
    public static UpdateCheckResult Available(string version, string? notes, long size) => new(UpdateCheckStatus.UpdateAvailable, version, notes, size);
    public static UpdateCheckResult Fail(string message) => new(UpdateCheckStatus.Failed, Message: message);
    public static UpdateCheckResult NotInstalled(string message) => new(UpdateCheckStatus.NotInstalled, Message: message);
}

/// <summary>Checks for, downloads and applies new Helm versions. All members are safe to call from the UI thread.</summary>
public interface IUpdateService
{
    string CurrentVersion { get; }

    /// <summary>False for dev/portable builds; every update action is then disabled with <see cref="NotInstalledReason"/>.</summary>
    bool IsInstalled { get; }

    string? NotInstalledReason { get; }

    UpdateState State { get; }

    DateTimeOffset? LastChecked { get; }

    UpdateCheckResult? LastResult { get; }

    /// <summary>0–100 while <see cref="State"/> is Downloading.</summary>
    int DownloadProgress { get; }

    /// <summary>Last network/GitHub failure, shown quietly (never modal).</summary>
    string? ErrorMessage { get; }

    /// <summary>Raised (on any thread) whenever any of the properties above change.</summary>
    event EventHandler? StateChanged;

    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);

    Task DownloadAsync(IProgress<int>? progress, CancellationToken ct);

    /// <summary>Disables every module, applies the downloaded update and restarts Helm.</summary>
    Task ApplyAndRestartAsync();
}
