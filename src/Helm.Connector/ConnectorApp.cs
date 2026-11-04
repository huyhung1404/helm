using System.Text;
using Helm.Connector.Commands;
using Helm.Shell.Services;

namespace Helm.Connector;

/// <summary>Where a command reads and writes, and what it reads of its environment (the real console, or a test's).</summary>
internal sealed record CliConsole(TextWriter Out, TextWriter Error, Func<string, string?> Environment)
{
    public static CliConsole System { get; } = new(Console.Out, Console.Error, global::System.Environment.GetEnvironmentVariable);

    /// <summary>The raw input and output, for <c>mcp</c>'s relay.</summary>
    public Func<Stream> OpenInput { get; init; } = Console.OpenStandardInput;

    public Func<Stream> OpenOutput { get; init; } = Console.OpenStandardOutput;

    /// <summary>Asks for a secret (the passphrase) without showing it; null when there is no answer.</summary>
    public Func<string, string?> ReadSecret { get; init; } = ReadHidden;

    private static string? ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        // Piped in (a script): one line, as typed.
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }
}

/// <summary>One command of helm-connector (<c>status</c>, <c>serve</c>…).</summary>
internal interface ICliCommand
{
    string Name { get; }

    /// <summary>One line for <c>helm-connector --help</c>.</summary>
    string Summary { get; }

    /// <returns>The exit code: 0 done, 1 failed, 2 used wrongly.</returns>
    Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>What every command gets: the console, the data folder and <c>--sync</c> as the global options gave them.</summary>
internal sealed record CliContext(CliConsole Console, string DataDirectory, string? SyncOption = null)
{
    /// <exception cref="FormatException">An unknown tool in --sync or connector.json.</exception>
    public SyncSelection Sync => SyncSelection.Resolve(SyncOption, DataDirectory);
}

/// <summary>
/// The command line: <c>helm-connector [--data-dir PATH] [--sync LIST] &lt;command&gt; [its options]</c>,
/// <c>--version</c>, <c>--help</c>. A new command is one <see cref="ICliCommand"/> added to <see cref="Commands"/>.
/// </summary>
internal static class ConnectorApp
{
    public const string Name = "helm-connector";
    public const int Ok = 0, Failed = 1, Usage = 2;

    public static IReadOnlyList<ICliCommand> Commands { get; } = [new StatusCommand(), new SyncCommand(), new ServeCommand(), new McpCommand(), .. GateCommand.All()];

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliConsole console, CancellationToken ct = default)
    {
        string? dataDir = null, sync = null;
        var i = 0;
        for (; i < args.Count && args[i].StartsWith('-'); i++)
        {
            switch (args[i])
            {
                case "--version" or "-V":
                    console.Out.WriteLine($"{Name} {AppInfo.Version}");
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
                case "--sync" when i + 1 < args.Count:
                    sync = args[++i];
                    break;
                case var option when option.StartsWith("--sync=", StringComparison.Ordinal):
                    sync = option["--sync=".Length..];
                    break;
                default:
                    console.Error.WriteLine($"{Name}: unknown option '{args[i]}'. See {Name} --help.");
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
            console.Error.WriteLine($"{Name}: '{args[i]}' is not a command. See {Name} --help.");
            return Usage;
        }
        var context = new CliContext(console, DataDirectory.Resolve(dataDir, console.Environment), sync);
        try
        {
            return await command.RunAsync(context, [.. args.Skip(i + 1)], ct);
        }
        catch (FormatException ex)
        {
            console.Error.WriteLine($"{Name}: {ex.Message}");
            return Usage;
        }
    }

    private static void Help(TextWriter output)
    {
        output.WriteLine($"{Name} {AppInfo.Version}: Helm's data on a home server, in sync, with Helm's tools over MCP.");
        output.WriteLine();
        output.WriteLine($"Usage: {Name} [--data-dir PATH] [--sync LIST] <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        foreach (var command in Commands) output.WriteLine($"  {command.Name,-10} {command.Summary}");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine($"  --data-dir PATH  Helm's data folder (else ${DataDirectory.Variable}, else ~/.local/share/helm)");
        output.WriteLine($"  --sync LIST      What to sync, e.g. ai,wallet,tracker,habits,notes (else {SyncSelection.FileName}, else");
        output.WriteLine($"                   {string.Join(",", SyncSelection.Default.Select(g => g.TrimEnd('.')))})");
        output.WriteLine("  --version        Print the version");
        output.WriteLine("  --help           Print this help");
    }
}
