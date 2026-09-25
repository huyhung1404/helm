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

/// <summary>Checks for, downloads and applies new Helm versions.</summary>
public interface IUpdateService
{
    string CurrentVersion { get; }

    DateTimeOffset? LastChecked { get; }

    UpdateCheckResult? LastResult { get; }

    event EventHandler? StateChanged;

    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);
}
