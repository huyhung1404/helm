using Helm.Connector.Commands;
using Helm.Shell.Services;

namespace Helm.Connector;

/// <summary>Where a command writes and what it reads of its environment (the real console, or a test's).</summary>
internal sealed record CliConsole(TextWriter Out, TextWriter Error, Func<string, string?> Environment)
{
    public static CliConsole System { get; } = new(Console.Out, Console.Error, global::System.Environment.GetEnvironmentVariable);
}

/// <summary>One command of helm (<c>helm status</c>, <c>helm serve</c>…).</summary>
internal interface ICliCommand
{
    string Name { get; }

    /// <summary>One line for <c>helm --help</c>.</summary>
    string Summary { get; }

    /// <returns>The exit code: 0 done, 1 failed, 2 used wrongly.</returns>
    Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>What every command gets: the console and the data folder chosen by the global options.</summary>
internal sealed record CliContext(CliConsole Console, string DataDirectory);

/// <summary>
/// helm's command line: <c>helm [--data-dir PATH] &lt;command&gt; [its options]</c>, <c>helm --version</c>,
/// <c>helm --help</c>. A new command is one <see cref="ICliCommand"/> added to <see cref="Commands"/>.
/// </summary>
internal static class ConnectorApp
{
    public const int Ok = 0, Failed = 1, Usage = 2;

    public static IReadOnlyList<ICliCommand> Commands { get; } = [new StatusCommand(), new ServeCommand()];

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliConsole console, CancellationToken ct = default)
    {
        string? dataDir = null;
        var i = 0;
        for (; i < args.Count && args[i].StartsWith('-'); i++)
        {
            switch (args[i])
            {
                case "--version" or "-V":
                    console.Out.WriteLine($"helm {AppInfo.Version}");
                    return Ok;
                case "--help" or "-h":
                    Help(console.Out);
                    return Ok;
                case "--data-dir" when i + 1 < args.Count:
                    dataDir = args[++i];
                    break;
                case var option when option.StartsWith("--data-dir=", StringComparison.Ordinal):
                    dataDir = option["--data-dir=".Length..];
                    break;
                default:
                    console.Error.WriteLine($"helm: unknown option '{args[i]}'. See helm --help.");
                    return Usage;
            }
        }
        if (i == args.Count || args[i] == "help")
        {
            Help(console.Out);
            return i == args.Count ? Usage : Ok;
        }
        if (Commands.FirstOrDefault(c => c.Name == args[i]) is not { } command)
        {
            console.Error.WriteLine($"helm: '{args[i]}' is not a command. See helm --help.");
            return Usage;
        }
        var context = new CliContext(console, DataDirectory.Resolve(dataDir, console.Environment));
        return await command.RunAsync(context, [.. args.Skip(i + 1)], ct);
    }

    private static void Help(TextWriter output)
    {
        output.WriteLine($"helm {AppInfo.Version}: Helm without a screen (Helm Sync, AI agents for the phone).");
        output.WriteLine();
        output.WriteLine("Usage: helm [--data-dir PATH] <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        foreach (var command in Commands) output.WriteLine($"  {command.Name,-10} {command.Summary}");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine($"  --data-dir PATH  Helm's data folder (else ${DataDirectory.Variable}, else ~/.local/share/helm)");
        output.WriteLine("  --version        Print the version");
        output.WriteLine("  --help           Print this help");
    }
}
