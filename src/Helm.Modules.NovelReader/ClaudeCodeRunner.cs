using System.IO;
using System.Text;
using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>Claude Code could not do the job: not installed, not connected to Helm, stopped with an error, too slow.</summary>
public sealed class ClaudeCodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs Claude Code (the user's own Claude plan, no API key) without a window (<c>claude -p</c>), allowed only some of
/// Helm's MCP tools, for Novel Reader's AI helpers. Claude Code reaches Helm's MCP server, which runs inside this Helm, so
/// what it writes arrives in the library while it works. Needs Claude Code installed and Helm added to it (the AI &amp;
/// MCP page). The instructions go in on standard input, never through cmd.exe; it runs with the user's normal rights even
/// when Helm is elevated, and stops with Helm.
/// </summary>
public sealed class ClaudeCodeRunner(McpClientConfig mcp, IChildProcessLauncher launcher, ILogger<ClaudeCodeRunner> logger)
{
    /// <summary>MCP tools are named mcp__&lt;server&gt;__&lt;tool&gt; in Claude Code.</summary>
    public static IReadOnlyList<string> ToolNames(params string[] tools) =>
        tools.Select(t => $"mcp__{McpClientConfig.ServerName}__{t}").ToList();

    /// <summary>Why Claude Code cannot run now, or null when it can.</summary>
    public string? Unavailable()
    {
        if (!mcp.Enabled) return "Helm's AI & MCP is turned off; turn it on on the AI & MCP page.";
        if (McpClientConfig.FindClaude() is null) return "Claude Code is not installed on this PC.";
        return mcp.Status(McpClientKind.ClaudeCode).State switch
        {
            McpClientState.Added => null,
            McpClientState.AddedElsewhere => "Claude Code starts another Helm.exe; press Add for me on the AI & MCP page.",
            McpClientState.Unreadable => "Claude Code's settings file cannot be read.",
            _ => "Helm is not added to Claude Code yet; press Add for me on the AI & MCP page.",
        };
    }

    /// <summary>
    /// Runs Claude Code on <paramref name="instructions"/> and returns its last answer. Each tool call reports
    /// <paramref name="describe"/>(tool, calls so far) to <paramref name="progress"/>.
    /// </summary>
    /// <exception cref="ClaudeCodeException">It could not start, failed, or did not finish within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> stopped it.</exception>
    public async Task<string> RunAsync(IReadOnlyList<string> allowedTools, string instructions, Func<string, int, string> describe,
        IProgress<string>? progress, TimeSpan timeout, CancellationToken ct)
    {
        if (Unavailable() is { } problem) throw new ClaudeCodeException(problem);
        var claude = McpClientConfig.FindClaude()!;
        IReadOnlyList<string> args = ["-p", "--output-format", "stream-json", "--verbose", "--allowedTools", string.Join(",", allowedTools)];
        string commandLine;
        if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm's claude.cmd only runs through cmd.exe; these arguments hold nothing cmd.exe reads itself.
            var comSpec = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe";
            commandLine = $"{CommandLine.Quote(comSpec)} /d /s /c \"{CommandLine.Join([claude, .. args])}\"";
        }
        else
        {
            commandLine = CommandLine.Join([claude, .. args]);
        }

        ChildProcess process;
        try
        {
            process = launcher.Start(new ChildProcessStartInfo(commandLine, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        }
        catch (ChildProcessException ex)
        {
            throw new ClaudeCodeException("Claude Code could not be started: " + ex.Message, ex);
        }
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            using var stop = limit.Token.Register(() =>
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            });
            await using (var input = new StreamWriter(process.StandardInput, new UTF8Encoding(false)))
                await input.WriteAsync(instructions).ConfigureAwait(false);
            var errors = ReadAllAsync(process.StandardError);
            var (result, failed, calls) = await ReadEventsAsync(process.StandardOutput, progress, describe).ConfigureAwait(false);
            var exitCode = await process.Exited.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (limit.IsCancellationRequested) throw new ClaudeCodeException($"Claude Code did not finish within {timeout.TotalMinutes:0} minutes.");
            logger.LogInformation("Claude Code made {Calls} tool calls and exited with {ExitCode}", calls, exitCode);
            if (failed || exitCode != 0)
            {
                var why = (result ?? await errors.ConfigureAwait(false)).Trim();
                throw new ClaudeCodeException("Claude Code stopped: " + (why.Length > 0 ? why[..Math.Min(why.Length, 300)] : $"exit code {exitCode}."));
            }
            return result ?? "";
        }
    }

    /// <summary>
    /// Reads Claude Code's stream of JSON lines: each tool call moves the progress on; the last line is the result
    /// (its text, and whether it is an error).
    /// </summary>
    internal static async Task<(string? Result, bool Failed, int Calls)> ReadEventsAsync(Stream output, IProgress<string>? progress,
        Func<string, int, string> describe)
    {
        using var reader = new StreamReader(output, Encoding.UTF8);
        string? result = null;
        var failed = false;
        var calls = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "assistant" && root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_use") continue;
                        calls++;
                        var tool = block.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Split("__")[^1] : "";
                        progress?.Report(describe(tool, calls));
                    }
                }
                else if (type == "result")
                {
                    result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    failed = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                }
            }
            catch (JsonException)
            {
                // Not one of its events (a warning line): skipped.
            }
        }
        return (result, failed, calls);
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        return text.Length > 2000 ? text[..2000] : text;
    }
}
