namespace Helm.Connector.Status;

/// <summary><c>helm-connector status</c> as text: one fact a line, and the next command to run where something is missing.</summary>
internal static class StatusPrinter
{
    public static void Write(StatusReport report, TextWriter output, DateTimeOffset now)
    {
        output.WriteLine($"{ConnectorApp.Name} {report.Version} on {report.Device}");
        output.WriteLine($"Data       {report.DataDirectory}{(report.DataDirectoryExists ? "" : " (not created yet)")}");
        output.WriteLine($"Host key   {report.HostKey}");
        output.WriteLine($"Sync       {Sync(report.Sync)}");
        if (report.Sync.DeviceId is { } id) output.WriteLine($"Device id  {id}");
        output.WriteLine($"Syncs      {string.Join(", ", report.Groups.Select(g => $"{g.Name} ({g.Prefix})"))}");
        if (report.Sync.Problem is { } problem) output.WriteLine($"Problem    {problem}");

        if (report.Serve is not { } serve)
        {
            output.WriteLine("Serve      not running");
        }
        else
        {
            var synced = serve.LastSyncedAt is { } at ? $", last sync {Ago(now - at)}" : "";
            output.WriteLine($"Serve      running since {serve.Started.ToLocalTime():yyyy-MM-dd HH:mm} (pid {serve.Pid}): {serve.State}{(serve.Live ? ", live" : "")}{synced}");
            if (serve.LastError is { } error) output.WriteLine($"Last error {error}");
            output.WriteLine(serve.Breaker is { } reason
                ? $"Throttle   TRIPPED {Ago(now - serve.BreakerAt.GetValueOrDefault(now))}: {reason}. Restart the service once the cause is fixed."
                : $"Throttle   ok (stops sync above {serve.MaxPushesPerMinute} pushes a minute or {Helm.Connector.Sync.PushBreaker.MaxPushesPerTenMinutes} in ten)");
            if (serve.McpSocket is { } socket) output.WriteLine($"MCP        {socket}");
        }

        output.WriteLine(report.Holds.Count == 0 ? "Holds      none" : "Holds      (approve or keep them on the phone or the PC)");
        foreach (var hold in report.Holds)
            output.WriteLine($"  {hold.Collection,-24} {hold.Deletions} deletions from another device wait ({hold.LiveRecords} records here)");
    }

    private static string Sync(SyncReport sync) =>
        !sync.Connected ? $"not connected ({ConnectorApp.Name} sync connect)"
        : !sync.Unlocked ? $"connected to {sync.Server}, locked ({ConnectorApp.Name} sync unlock)"
        : $"connected to {sync.Server}, unlocked";

    internal static string Ago(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? $"{Math.Max(0, (int)span.TotalSeconds)} s ago"
        : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes} min ago"
        : span < TimeSpan.FromDays(2) ? $"{(int)span.TotalHours} h ago"
        : $"{(int)span.TotalDays} days ago";
}
