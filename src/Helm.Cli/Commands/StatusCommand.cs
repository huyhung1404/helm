using System.Text.Json;
using Helm.Ai.Agents;
using Helm.Cli.Hosting;
using Helm.Cli.Status;
using Helm.Core.Settings;

namespace Helm.Cli.Commands;

/// <summary><c>helm status [--json]</c>: the data folder, sync, the agents and the workers, without changing anything.</summary>
internal sealed class StatusCommand : ICliCommand
{
    public string Name => "status";

    public string Summary => "Show the data folder, sync, the agents and whether the worker is online";

    public Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var json = args.Contains("--json");
        if (args.Any(a => a != "--json"))
        {
            context.Console.Error.WriteLine("Usage: helm status [--json]");
            return Task.FromResult(CliApp.Usage);
        }
        var paths = new HelmPaths(context.DataDirectory);
        var secrets = CliSecrets.For(paths, context.Console.Environment);
        var report = new StatusProbe(paths, secrets, secrets.KeySource, AgentCommands.Of, Environment.MachineName).Probe();
        if (json) context.Console.Out.WriteLine(JsonSerializer.Serialize(report, Json));
        else StatusPrinter.Write(report, context.Console.Out, DateTimeOffset.UtcNow);
        return Task.FromResult(CliApp.Ok);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
