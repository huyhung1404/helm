using Helm.Ai.Assistant;
using Helm.Connector.Hosting;
using Helm.Connector.Mcp;
using Helm.Connector.Sync;
using Helm.Core.Linux;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Commands;

/// <summary>
/// <c>helm-connector serve</c>: keeps Helm's data on this box in sync (live) for the tools in the <see cref="SyncSelection"/>,
/// and serves their tools over MCP on a socket for <c>helm-connector mcp</c>, until stopped (Ctrl+C, or SIGTERM from
/// systemd). Unattended: pushes go through a breaker, held deletions are only reported, and both ping the health
/// check (<c>$HELM_HC_URL</c>). It runs no agent: Fairy Core does.
/// </summary>
internal sealed class ServeCommand : ICliCommand
{
    public string Name => "serve";

    public string Summary => "Keep Helm's data in sync and serve its tools over MCP (for a systemd service)";

    public async Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count > 0)
        {
            context.Console.Error.WriteLine($"Usage: {ConnectorApp.Name} serve");
            return ConnectorApp.Usage;
        }
        var env = context.Console.Environment;
        var paths = new HelmPaths(context.DataDirectory);
        var selection = context.Sync;
        PrivateFiles.CreateDirectory(paths.Root);
        using var loggers = new JournalLoggerFactory(context.Console.Error, JournalLoggerFactory.LevelFrom(env("HELM_LOG_LEVEL")));
        var log = loggers.CreateLogger("helm-connector");
        var health = HealthPing.From(env, log);
        await using var services = ConnectorHost.Build(paths, ConnectorSecrets.For(paths, env), loggers, selection);

        if (services.GetRequiredService<SyncSetupService>().Stage != SyncSetupStage.Ready)
        {
            context.Console.Error.WriteLine($"{ConnectorApp.Name} serve: Helm Sync is not set up on this box. Run {ConnectorApp.Name} sync connect, then sync unlock.");
            return ConnectorApp.Failed;
        }
        var engine = services.GetRequiredService<SyncEngine>();
        var breaker = services.GetRequiredService<PushBreaker>();
        engine.SetIncluded(selection.Prefixes);

        // One gate for every connection: the yes may come on another one (helm-connector confirm) than the call.
        var gate = new ConfirmationGate(logger: loggers.CreateLogger<ConfirmationGate>());
        var mcp = new McpPart(new McpSocketHost(ConnectorPaths.McpSocket(paths, env),
            () => ConnectorMcp.Server(services.GetServices<IMcpToolProvider>(), gate, loggers.CreateLogger<McpServer>()),
            loggers.CreateLogger<McpSocketHost>()));
        // Parts registered in the container join them.
        IServePart[] parts =
        [
            new HoldWatcher(engine, health, loggers.CreateLogger<HoldWatcher>()), mcp,
            .. services.GetServices<IServePart>(),
        ];
        var started = DateTimeOffset.UtcNow;
        void Save()
        {
            try
            {
                new ServeState
                {
                    Pid = Environment.ProcessId,
                    Started = started,
                    Sync = selection.Prefixes,
                    SyncState = engine.Status.State.ToString(),
                    LastSyncedAt = engine.Status.LastSyncedAt,
                    LastError = engine.Status.LastError,
                    Live = engine.IsLive,
                    Breaker = breaker.TrippedReason,
                    BreakerAt = breaker.TrippedAt,
                    McpSocket = mcp.Path,
                }.Write(paths);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not write the serve state for status");
            }
        }
        breaker.Tripped += reason =>
        {
            log.LogCritical("Sync stopped: {Reason}", reason);
            _ = health.FailAsync("helm-connector stopped syncing: " + reason);
            Save();
        };
        engine.StatusChanged += (_, _) => Save();

        try
        {
            foreach (var part in parts) part.Start();
        }
        catch (InvalidOperationException ex)
        {
            foreach (var part in parts) part.Dispose();
            context.Console.Error.WriteLine($"{ConnectorApp.Name} serve: {ex.Message}");
            return ConnectorApp.Failed;
        }
        engine.Start();
        engine.SetLive(true);
        Save();
        log.LogInformation("Serving {Device} ({Data}): syncing {Sync}; MCP at {Socket}", Environment.MachineName, paths.Root,
            string.Join(", ", selection.Prefixes), mcp.Path);

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        log.LogInformation("Stopping");
        foreach (var part in parts) part.Dispose();
        if (breaker.TrippedReason is null)
        {
            try
            {
                // What the last tool calls changed goes up before the process ends.
                using var last = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await engine.SyncNowAsync(last.Token);
            }
            catch (OperationCanceledException)
            {
                log.LogDebug("The last sync before stopping did not finish");
            }
        }
        try { ServeState.Remove(paths); }
        catch (IOException) { }
        return ConnectorApp.Ok;
    }
}
