namespace Helm.Cli.Status;

/// <summary><c>helm status</c> as text: one fact a line, and the next command to run where something is missing.</summary>
internal static class StatusPrinter
{
    public static void Write(StatusReport report, TextWriter output, DateTimeOffset now)
    {
        output.WriteLine($"helm {report.Version} on {report.Device}");
        output.WriteLine($"Data       {report.DataDirectory}{(report.DataDirectoryExists ? "" : " (not created yet)")}");
        output.WriteLine($"Host key   {report.HostKey}");
        output.WriteLine($"Sync       {Sync(report.Sync)}");
        if (report.Sync.DeviceId is { } id) output.WriteLine($"Device id  {id}");
        if (report.Sync.Problem is { } problem) output.WriteLine($"Problem    {problem}");

        output.WriteLine();
        output.WriteLine("Agent CLIs");
        foreach (var cli in report.Clis) output.WriteLine($"  {cli.Name,-12} {cli.Path ?? "not found"}");

        output.WriteLine();
        output.WriteLine("Agents on this box (in the order they take jobs)");
        if (report.Agents.Count == 0) output.WriteLine("  none yet");
        foreach (var agent in report.Agents) output.WriteLine($"  {agent.Name,-12} {agent.Kind,-12} {agent.State}");

        output.WriteLine();
        output.WriteLine("Workers (devices that take the phone's agent jobs)");
        if (report.Workers.Count == 0) output.WriteLine(report.Sync.Unlocked ? "  none heard of yet" : "  unknown until sync is set up");
        foreach (var worker in report.Workers)
        {
            var name = worker.ThisDevice ? $"{worker.Device} (this box)" : worker.Device;
            var state = worker.Online ? "online" : "offline";
            var agents = worker.Problem ?? (worker.Agents.Count > 0 ? string.Join(", ", worker.Agents) : "no agent ready");
            output.WriteLine($"  {name,-24} {state}, seen {Ago(now - worker.SeenAt)}: {agents}");
        }
    }

    private static string Sync(SyncReport sync) =>
        !sync.Connected ? "not connected"
        : !sync.Unlocked ? $"connected to {sync.Server}, locked (no account key here)"
        : $"connected to {sync.Server}, unlocked";

    internal static string Ago(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? $"{Math.Max(0, (int)span.TotalSeconds)} s ago"
        : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes} min ago"
        : span < TimeSpan.FromDays(2) ? $"{(int)span.TotalHours} h ago"
        : $"{(int)span.TotalDays} days ago";
}
