using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.App.Mcp;

/// <summary>What running <c>claude</c> gave: whether it worked, and what it printed (shown to the user, never logged).</summary>
internal sealed record McpClientSetupResult(bool Succeeded, string Output);

/// <summary>
/// Adds Helm to Claude Code (or removes it) by running <c>claude mcp add/remove</c>. The program gets an argument list,
/// not a line for a shell, and runs with the user's normal rights even when Helm is elevated, so it writes the same
/// <c>~/.claude.json</c> Claude Code reads.
/// </summary>
internal sealed partial class McpClientSetup(McpClientConfig config, IChildProcessLauncher launcher, ILogger<McpClientSetup> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The Claude Code CLI on PATH, or null when it is not installed.</summary>
    public string? FindClaude() => McpClientConfig.FindClaude();

    /// <summary>Adds Helm for every folder; an older <c>helm</c> entry (another Helm.exe) is replaced.</summary>
    public async Task<McpClientSetupResult> AddAsync(CancellationToken ct)
    {
        if (config.Status(McpClientKind.ClaudeCode).State != McpClientState.NotAdded)
        {
            var removed = await RunAsync(McpClientConfig.ClaudeCodeRemoveArguments, ct);
            if (!removed.Succeeded) return removed;
        }
        return await RunAsync(config.ClaudeCodeAddArguments, ct);
    }

    public Task<McpClientSetupResult> RemoveAsync(CancellationToken ct) => RunAsync(McpClientConfig.ClaudeCodeRemoveArguments, ct);

    private async Task<McpClientSetupResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (FindClaude() is not { } claude) return new(false, NotInstalled);
        string commandLine;
        if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm's claude.cmd only runs through cmd.exe, which reads & | < > ^ % itself: such arguments are refused, not escaped.
            if (args.FirstOrDefault(a => !CmdSafe().IsMatch(a)) is { } unsafeArg)
                return new(false, $"Claude Code is installed as claude.cmd, and Helm will not pass “{unsafeArg}” through cmd.exe. Copy the command and run it in a terminal instead.");
            var comSpec = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe";
            commandLine = $"{CommandLine.Quote(comSpec)} /d /s /c \"{CommandLine.Join([claude, .. args])}\"";
        }
        else
        {
            commandLine = CommandLine.Join([claude, .. args]);
        }

        try
        {
            using var process = launcher.Start(new ChildProcessStartInfo(commandLine, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            process.StandardInput.Close(); // it never asks anything here
            var stdout = ReadAllAsync(process.StandardOutput);
            var stderr = ReadAllAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            int exitCode;
            try
            {
                exitCode = await process.Exited.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return new(false, "Claude Code did not finish within a minute. Copy the command and run it in a terminal instead.");
            }
            var output = Ansi().Replace((await stdout).Trim() + "\n" + (await stderr).Trim(), "").Trim();
            logger.LogInformation("claude {Verb} {Subverb} exited with {ExitCode}", args[0], args.ElementAtOrDefault(1), exitCode);
            return new(exitCode == 0, output.Length > 0 ? output : $"claude exited with code {exitCode}.");
        }
        catch (ChildProcessException ex)
        {
            logger.LogWarning(ex, "Could not start Claude Code");
            return new(false, "Claude Code could not be started: " + ex.Message);
        }
    }

    public const string NotInstalled =
        "Claude Code was not found on PATH. Install it (Anthropic's Claude Code setup guide has the one-line installer), " +
        "open a new terminal and check that “claude --version” works, then press Add for me again.";

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        return text.Length > 4000 ? text[..4000] + "…" : text;
    }

    /// <summary>Colour codes a terminal would turn into colours.</summary>
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"^[A-Za-z0-9 ._:=,/\\@+\-\[\]]*$")]
    private static partial Regex CmdSafe();
}
