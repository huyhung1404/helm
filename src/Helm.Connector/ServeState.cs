using System.Diagnostics;
using System.Text.Json;
using Helm.Core.Linux;
using Helm.Core.Settings;

namespace Helm.Connector;

/// <summary>
/// What a running <c>helm-connector serve</c> says about itself in &lt;data&gt;/run/serve.json, for <c>status</c> in
/// another process: its sync state, the breaker and the holds. Written on every change, removed when serve stops.
/// </summary>
internal sealed record ServeState
{
    public int Pid { get; init; }

    public DateTimeOffset Started { get; init; }

    public IReadOnlyList<string> Sync { get; init; } = [];

    public string SyncState { get; init; } = "";

    public DateTimeOffset? LastSyncedAt { get; init; }

    public string? LastError { get; init; }

    public bool Live { get; init; }

    /// <summary>Why the push breaker tripped (sync stopped until a restart), or null.</summary>
    public string? Breaker { get; init; }

    public DateTimeOffset? BreakerAt { get; init; }

    public string? McpSocket { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public void Write(HelmPaths paths)
    {
        var file = ConnectorPaths.StateFile(paths);
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, file, overwrite: true);
    }

    public static void Remove(HelmPaths paths) => File.Delete(ConnectorPaths.StateFile(paths));

    /// <summary>The state of the serve running now, or null (none, or its process is gone).</summary>
    public static ServeState? Read(HelmPaths paths)
    {
        var file = ConnectorPaths.StateFile(paths);
        if (!File.Exists(file)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ServeState>(File.ReadAllText(file), Json);
            return state is not null && IsRunning(state.Pid) ? state : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
