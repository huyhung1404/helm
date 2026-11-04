namespace Helm.Connector.Status;

/// <summary>What <c>helm-connector status</c> found (also its <c>--json</c> output).</summary>
internal sealed record StatusReport
{
    public required string Version { get; init; }

    public required string Device { get; init; }

    public required string DataDirectory { get; init; }

    public bool DataDirectoryExists { get; init; }

    /// <summary>Where this box's host key is (or will be) kept.</summary>
    public required string HostKey { get; init; }

    public required SyncReport Sync { get; init; }

    /// <summary>What this box syncs (the allowlist), each with the tool's name.</summary>
    public IReadOnlyList<GroupReport> Groups { get; init; } = [];

    /// <summary>The running serve, or null when none runs.</summary>
    public ServeReport? Serve { get; init; }

    /// <summary>Deletions from another device that sync holds for the user, per collection.</summary>
    public IReadOnlyList<HoldReport> Holds { get; init; } = [];
}

/// <param name="Connected">This box has a sync token (a server to sync with).</param>
/// <param name="Unlocked">This box has the account's key: it reads and writes synced data.</param>
/// <param name="Problem">Why a file that is there could not be read here, or null.</param>
internal sealed record SyncReport(bool Connected, bool Unlocked, string? Server, string? DeviceId, string? Problem);

internal sealed record GroupReport(string Prefix, string Name);

/// <param name="Breaker">Why the push breaker tripped (sync stopped until a restart), or null.</param>
internal sealed record ServeReport(int Pid, DateTimeOffset Started, string State, DateTimeOffset? LastSyncedAt, string? LastError, bool Live,
    string? Breaker, DateTimeOffset? BreakerAt, int MaxPushesPerMinute, string? McpSocket);

internal sealed record HoldReport(string Collection, int Deletions, int LiveRecords);
