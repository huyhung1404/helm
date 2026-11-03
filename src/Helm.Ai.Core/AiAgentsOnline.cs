using Helm.Ai.Assistant;
using Helm.Core.Sync;

namespace Helm.Ai;

/// <summary>
/// The Helm PCs whose agents take jobs now, from their heartbeats (<see cref="AgentJobs.WorkersCollection"/>), as the
/// AI settings' summary says it: "Agents online: HUNG-PC".
/// </summary>
public static class AiAgentsOnline
{
    public const string None = "No agent online";

    public static string Text(IEnumerable<AiWorkerRecord> workers, DateTimeOffset now)
    {
        var online = workers.Where(w => now - w.SeenAt < AgentJobs.Online).OrderBy(w => w.Device, StringComparer.CurrentCultureIgnoreCase).ToList();
        var ready = online.Where(w => w.Problem is null).Select(w => w.Device).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (ready.Count > 0) return "Agents online: " + string.Join(", ", ready);
        // Heard from, but none takes a job now (Claude Code at its limit…): say why, once.
        return online.Count > 0 ? $"No agent ready · {online[0].Device}: {online[0].Problem!.TrimEnd('.')}" : None;
    }
}
