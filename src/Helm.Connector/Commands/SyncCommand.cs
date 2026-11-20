using Helm.Connector.Hosting;
using Helm.Core.Linux;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Connector.Commands;

/// <summary>
/// <c>helm-connector sync pair</c>, <c>sync connect</c> and <c>sync unlock</c>: this box joins the user's Helm Sync
/// account as the apps' General → Sync does (<see cref="SyncSetupService"/>), its token and the account key kept under
/// the box's host key. <c>pair</c> shows a code to type into Helm on a PC where sync is unlocked, so nobody types the
/// passphrase on the box. Every first sync brings the <see cref="SyncSelection"/> only.
/// </summary>
internal sealed class SyncCommand : ICliCommand
{
    private const string Usage =
        "Usage: helm-connector sync pair [--device NAME] [--server URL]   (shows a code to type into Helm on a PC)\n" +
        "       helm-connector sync connect --token TOKEN [--server URL]\n" +
        "       helm-connector sync connect --invite CODE --account NAME [--device NAME] [--server URL]\n" +
        "       helm-connector sync unlock [--recovery-key KEY]   (asks for the passphrase without one)";

    public string Name => "sync";

    public string Summary => "join Helm Sync: pair by a code typed into Helm on a PC, or a token / invite and the passphrase";

    /// <summary>How often <c>pair</c> says it is still waiting.</summary>
    internal static TimeSpan WaitingNoticeEvery { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often <c>pair</c> asks the server for the package (tests shorten it).</summary>
    internal static TimeSpan PairPollEvery { get; set; } = TimeSpan.FromSeconds(2);

    public async Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var console = context.Console;
        if (args.Count == 0 || args[0] is not ("connect" or "unlock" or "pair") || Options(args.Skip(1).ToList()) is not { } options)
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
        // Before anything can sync: unlocking and pairing run a first sync at once, which must bring the selection only.
        services.GetRequiredService<SyncEngine>().SetIncluded(selection.Prefixes);
        var setup = services.GetRequiredService<SyncSetupService>();
        try
        {
            return args[0] switch
            {
                "connect" => await ConnectAsync(setup, options, console, ct),
                "pair" => await PairAsync(setup, services, selection, options, console, ct),
                _ => await UnlockAsync(setup, services, selection, options, console, ct),
            };
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

    private static async Task<int> PairAsync(SyncSetupService setup, IServiceProvider services, SyncSelection selection,
        IReadOnlyDictionary<string, string> options, CliConsole console, CancellationToken ct)
    {
        if (setup.Stage != SyncSetupStage.NotConnected)
        {
            console.Error.WriteLine($"This box is already connected to Helm Sync ({setup.Server}).");
            return ConnectorApp.Failed;
        }
        var server = options.TryGetValue("server", out var url) ? new Uri(url) : null;
        var device = options.GetValueOrDefault("device") ?? Environment.MachineName;
        using var offer = await setup.OfferPairingAsync(device, server, ct);
        console.Out.WriteLine($"Pairing code for \"{offer.DeviceName}\" (valid until {offer.ExpiresAt.ToLocalTime():HH:mm:ss}):");
        console.Out.WriteLine();
        console.Out.WriteLine("    " + SyncPairing.FormatCode(offer.Code));
        console.Out.WriteLine();
        console.Out.WriteLine($"In Helm on a PC where sync is unlocked: General → Sync → Devices → Add a device with a code. Type the code, check the name \"{offer.DeviceName}\", approve.");
        console.Out.WriteLine("Waiting…");
        console.Out.Flush();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var notices = SayStillWaitingAsync(offer, console, stop.Token);
        SyncAccountInfo account;
        try
        {
            account = await setup.CompletePairingAsync(offer, PairPollEvery, ct);
        }
        finally
        {
            await stop.CancelAsync();
            await notices;
        }
        console.Out.WriteLine($"Paired: account {account.AccountName}, as device {account.TokenName}. First sync of {string.Join(", ", selection.Prefixes)}…");
        var engine = services.GetRequiredService<SyncEngine>();
        var result = await engine.SyncNowAsync(ct);
        console.Out.WriteLine($"{result.Outcome}{(engine.Status.LastError is { } e ? $" ({e})" : "")}.");
        return result.Outcome is SyncRunOutcome.Completed or SyncRunOutcome.Held ? ConnectorApp.Ok : ConnectorApp.Failed;
    }

    /// <summary>Never waits in silence: how long the code still lives, every <see cref="WaitingNoticeEvery"/>.</summary>
    private static async Task SayStillWaitingAsync(SyncPairingOffer offer, CliConsole console, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(WaitingNoticeEvery, ct);
                var left = offer.ExpiresAt - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero) return;
                console.Out.WriteLine($"Still waiting for the code to be typed into Helm ({left:m\\:ss} left)…");
                console.Out.Flush();
            }
        }
        catch (OperationCanceledException)
        {
        }
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
