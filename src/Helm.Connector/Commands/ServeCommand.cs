using Helm.Ai.Agents;
using Helm.Ai.Assistant;
using Helm.Connector.Hosting;
using Helm.Core.Linux;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Commands;

/// <summary>
/// <c>helm serve</c>: what Helm on the PC does in the background for the phone, without a screen, until stopped
/// (Ctrl+C, or SIGTERM from systemd). Helm Sync stays live, the agents added here take the phone's agent jobs
/// (<see cref="AgentJobWorker"/>) with the tools' own stores, and the VPS worker runs when it is set up.
/// </summary>
internal sealed class ServeCommand : ICliCommand
{
    public string Name => "serve";

    public string Summary => "Run the agent worker and sync in the foreground (for a systemd service)";

    public async Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count > 0)
        {
            context.Console.Error.WriteLine("Usage: helm serve");
            return ConnectorApp.Usage;
        }
        var paths = new HelmPaths(context.DataDirectory);
        PrivateFiles.CreateDirectory(paths.Root);
        var secrets = ConnectorSecrets.For(paths, context.Console.Environment);
        using var loggers = new JournalLoggerFactory(context.Console.Error, JournalLoggerFactory.LevelFrom(context.Console.Environment("HELM_LOG_LEVEL")));
        var log = loggers.CreateLogger("helm");
        await using var services = ConnectorHost.Build(paths, secrets, loggers, s =>
        {
            s.AddAgentProxy(paths, _ => new TerminalAgentSignIn(context.Console.Out));
            HeadlessTools.Add(s);
        });

        if (services.GetRequiredService<SyncSetupService>().Stage != SyncSetupStage.Ready)
        {
            context.Console.Error.WriteLine("helm serve: Helm Sync is not set up on this box (connected and unlocked). See helm status.");
            return ConnectorApp.Failed;
        }
        var engine = services.GetRequiredService<SyncEngine>();
        engine.Start();
        engine.SetLive(true);
        var agents = services.GetRequiredService<AgentRegistry>();
        var worker = services.GetRequiredService<AgentJobWorker>();
        worker.Start();
        var vps = services.GetRequiredService<Helm.Ai.Vps.VpsWorker>();
        services.GetRequiredService<Helm.Ai.Vps.VpsWorkerControl>().Apply();
        // A sign-in or a change of the list tells the phone at once instead of at the next heartbeat.
        agents.Changed += (_, _) =>
        {
            worker.Beat();
            vps.SendStatus();
        };
        log.LogInformation("Serving {Device} ({Data}): agents ready: {Agents}", Environment.MachineName, paths.Root,
            agents.Ready() is { Count: > 0 } ready ? string.Join(", ", ready) : "none");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        log.LogInformation("Stopping");
        // Its record goes, and the phone hears of it at once: it asks the PC (or the API) instead.
        worker.Dispose();
        try
        {
            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await engine.SyncNowAsync(last.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            log.LogDebug(ex, "The last sync before stopping did not finish");
        }
        return ConnectorApp.Ok;
    }
}
