using Helm.Connector.Hosting;
using Helm.Core.Linux;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Connector.Commands;

/// <summary>
/// <c>helm-connector sync connect</c> and <c>sync unlock</c>: this box joins the user's Helm Sync account as the apps'
/// General → Sync does (<see cref="SyncSetupService"/>), its token and the account key kept under the box's host key.
/// Unlocking runs a first sync of the <see cref="SyncSelection"/> only.
/// </summary>
internal sealed class SyncCommand : ICliCommand
{
    private const string Usage =
        "Usage: helm-connector sync connect --token TOKEN [--server URL]\n" +
        "       helm-connector sync connect --invite CODE --account NAME [--device NAME] [--server URL]\n" +
        "       helm-connector sync unlock [--recovery-key KEY]   (asks for the passphrase without one)";

    public string Name => "sync";

    public string Summary => "connect this box to Helm Sync (token or invite), unlock it with the passphrase";

    public async Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var console = context.Console;
        if (args.Count == 0 || args[0] is not ("connect" or "unlock") || Options(args.Skip(1).ToList()) is not { } options)
        {
            console.Error.WriteLine(Usage);
            return ConnectorApp.Usage;
        }
        if (ServeState.Read(new HelmPaths(context.DataDirectory)) is { } serving)
        {
            console.Error.WriteLine($"{ConnectorApp.Name} serve is running (pid {serving.Pid}): stop it first.");
            return ConnectorApp.Failed;
        }
        var selection = context.Sync;
        var paths = new HelmPaths(context.DataDirectory);
        PrivateFiles.CreateDirectory(paths.Root);
        await using var services = ConnectorHost.Build(paths, ConnectorSecrets.For(paths, console.Environment), NullLoggerFactory.Instance, selection);
        var setup = services.GetRequiredService<SyncSetupService>();
        try
        {
            return args[0] == "connect" ? await ConnectAsync(setup, options, console, ct) : await UnlockAsync(setup, services, selection, options, console, ct);
        }
        catch (Exception ex) when (ex is SyncRequestException or SyncKeyException or SyncAuthException or HttpRequestException or UriFormatException)
        {
            console.Error.WriteLine($"{ConnectorApp.Name} sync {args[0]}: {ex.Message}");
            return ConnectorApp.Failed;
        }
    }

    private static async Task<int> ConnectAsync(SyncSetupService setup, IReadOnlyDictionary<string, string> options, CliConsole console, CancellationToken ct)
    {
        var server = options.TryGetValue("server", out var url) ? new Uri(url) : null;
        SyncAccountInfo account;
        if (options.TryGetValue("token", out var token)) account = await setup.ConnectWithTokenAsync(token, server, ct);
        else if (options.TryGetValue("invite", out var invite) && options.TryGetValue("account", out var name))
            account = await setup.RedeemInviteAsync(invite, name, options.GetValueOrDefault("device") ?? Environment.MachineName, server, ct);
        else
        {
            console.Error.WriteLine(Usage);
            return ConnectorApp.Usage;
        }
        console.Out.WriteLine($"Connected to {setup.Server}: account {account.AccountName}, as device {account.TokenName}.");
        console.Out.WriteLine($"Next: {ConnectorApp.Name} sync unlock");
        return ConnectorApp.Ok;
    }

    private static async Task<int> UnlockAsync(SyncSetupService setup, IServiceProvider services, SyncSelection selection,
        IReadOnlyDictionary<string, string> options, CliConsole console, CancellationToken ct)
    {
        if (setup.Stage == SyncSetupStage.NotConnected)
        {
            console.Error.WriteLine($"This box is not connected yet: {ConnectorApp.Name} sync connect first.");
            return ConnectorApp.Failed;
        }
        if (!await setup.HasPassphraseAsync(ct))
        {
            console.Error.WriteLine("This account has no passphrase yet: set Helm Sync up on the PC or the phone first.");
            return ConnectorApp.Failed;
        }
        if (options.TryGetValue("recovery-key", out var recovery)) await setup.UnlockWithRecoveryKeyAsync(recovery, ct);
        else if (console.ReadSecret("Helm Sync passphrase: ") is { Length: > 0 } passphrase) await setup.UnlockWithPassphraseAsync(passphrase, ct);
        else
        {
            console.Error.WriteLine("No passphrase given.");
            return ConnectorApp.Failed;
        }
        var engine = services.GetRequiredService<SyncEngine>();
        engine.SetIncluded(selection.Prefixes);
        console.Out.WriteLine($"Unlocked. First sync of {string.Join(", ", selection.Prefixes)}…");
        var result = await engine.SyncNowAsync(ct);
        console.Out.WriteLine($"{result.Outcome}: {result.Pulled} records down, {result.Pushed} up{(engine.Status.LastError is { } e ? $" ({e})" : "")}.");
        return result.Outcome is SyncRunOutcome.Completed or SyncRunOutcome.Held ? ConnectorApp.Ok : ConnectorApp.Failed;
    }

    /// <summary>"--name value" pairs; null for anything else.</summary>
    private static Dictionary<string, string>? Options(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count) return null;
            options[args[i][2..]] = args[i + 1];
        }
        return options;
    }
}
