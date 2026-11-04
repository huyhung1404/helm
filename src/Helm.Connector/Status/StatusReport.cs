namespace Helm.Connector.Status;

/// <summary>What <c>helm status</c> found (also its <c>--json</c> output).</summary>
internal sealed record StatusReport
{
    public required string Version { get; init; }

    public required string Device { get; init; }

    public required string DataDirectory { get; init; }

    public bool DataDirectoryExists { get; init; }

    /// <summary>Where this box's host key is (or will be) kept.</summary>
    public required string HostKey { get; init; }

    public required SyncReport Sync { get; init; }

    /// <summary>Each agent CLI Helm can run, and where it is installed (null: not found).</summary>
    public IReadOnlyList<CliReport> Clis { get; init; } = [];

    /// <summary>The agents added to this box's proxy, in the order they take jobs.</summary>
    public IReadOnlyList<AgentReport> Agents { get; init; } = [];

    /// <summary>The devices that take the phone's agent jobs, as the replica last heard of them; this box first.</summary>
    public IReadOnlyList<WorkerReport> Workers { get; init; } = [];
}

/// <param name="Connected">This box has a sync token (a server to sync with).</param>
/// <param name="Unlocked">This box has the account's key: it reads and writes synced data.</param>
/// <param name="Problem">Why a file that is there could not be read here, or null.</param>
internal sealed record SyncReport(bool Connected, bool Unlocked, string? Server, string? DeviceId, string? Problem);

internal sealed record CliReport(string Kind, string Name, string? Path);

/// <param name="State">"ready", "off", "not signed in" or "not installed".</param>
internal sealed record AgentReport(string Name, string Kind, bool Enabled, bool SignedIn, bool Installed, string State);

/// <param name="Agents">Its agents that take jobs now, best first.</param>
internal sealed record WorkerReport(string Device, bool ThisDevice, bool Online, DateTimeOffset SeenAt, IReadOnlyList<string> Agents, string? Problem);
