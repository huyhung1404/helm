using System.IO;
using System.Text;
using System.Text.Json;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Mcp;

/// <summary>
/// Runs Claude Code (the user's own Claude plan, no API key) without a window (<c>claude -p</c>) for Slack Report: one
/// question about text Helm hands it, with no tools or only reading (or editing) the folders named. The instructions go
/// in on standard input, never through cmd.exe; it runs with the user's normal rights even when Helm is elevated, and
/// stops with Helm.
/// </summary>
public sealed class ClaudeCodeRunner(IChildProcessLauncher launcher, ILogger<ClaudeCodeRunner> logger)
{
    /// <summary>Why <see cref="AskAsync"/> cannot run now, or null when it can (it needs Claude Code only, not Helm's MCP).</summary>
    public static string? AskUnavailable() => McpClientConfig.FindClaude() is null ? "Claude Code is not installed on this PC." : null;

    /// <summary>
    /// Asks Claude Code one question about text Helm hands it and returns the answer, with no tools at all: no files,
    /// no shell, no web, no MCP server (not even Helm's), and no hooks (so it never shows up as a Workspace session).
    /// For text from other people (Slack messages): whatever that text says, the answer is the only thing it can change.
    /// </summary>
    /// <param name="model">A Claude Code model name (haiku, sonnet), or null for the user's default.</param>
    /// <param name="effort">Claude Code's effort level (low, medium, high, xhigh, max), or null for its default.</param>
    /// <exception cref="ClaudeCodeException">It could not start, failed, or did not finish within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> stopped it.</exception>
    public Task<string> AskAsync(string instructions, string? model, TimeSpan timeout, CancellationToken ct, string? effort = null)
    {
        if (AskUnavailable() is { } problem) throw new ClaudeCodeException(problem);
        return StartAsync([.. AskArguments(model, effort)], instructions, timeout, ct);
    }

    /// <summary>
    /// Like <see cref="AskAsync"/>, but Claude Code may read <paramref name="folders"/> (the first is where it runs; only
    /// those: reading elsewhere asks a permission nobody gives) with Read, Grep and Glob: no shell, no edits, no web, no
    /// MCP, no hooks, and never .env files or keys (<see cref="SecretFiles"/>). For looking into projects to answer someone.
    /// </summary>
    public Task<string> AskInFolderAsync(string instructions, IReadOnlyList<string> folders, string? model, TimeSpan timeout, CancellationToken ct,
        string? effort = null)
    {
        if (AskUnavailable() is { } problem) throw new ClaudeCodeException(problem);
        var existing = Existing(folders);
        return StartAsync([.. FolderArguments(model, existing.Skip(1), effort)], instructions, timeout, ct, existing[0]);
    }

    /// <summary>
    /// Like <see cref="AskInFolderAsync"/>, but Claude Code may also create and edit files in <paramref name="folders"/>
    /// (Write, Edit; edits outside them ask a permission nobody gives): still no shell, no web, no MCP, no hooks, and
    /// never .env files or keys. For a task the user gave themself.
    /// </summary>
    public Task<string> WorkInFolderAsync(string instructions, IReadOnlyList<string> folders, string? model, TimeSpan timeout, CancellationToken ct,
        string? effort = null)
    {
        if (AskUnavailable() is { } problem) throw new ClaudeCodeException(problem);
        var existing = Existing(folders);
        return StartAsync([.. WorkArguments(model, existing.Skip(1), effort)], instructions, timeout, ct, existing[0]);
    }

    private static List<string> Existing(IReadOnlyList<string> folders)
    {
        var existing = folders.Where(Directory.Exists).ToList();
        return existing.Count > 0
            ? existing
            : throw new ClaudeCodeException($"The project folder {folders.FirstOrDefault()} does not exist.");
    }

    /// <summary>Files Claude Code never reads in a project, whatever it is asked.</summary>
    internal static readonly IReadOnlyList<string> SecretFiles =
    [
        "**/.env*", "**/*.keystore", "**/*.jks", "**/*.pem", "**/*.key", "**/*.p12", "**/*.pfx", "**/*.mobileprovision",
        "**/google-services.json", "**/GoogleService-Info.plist", "**/*secret*", "**/*credential*", "**/.git/**", "**/.ssh/**",
    ];

    internal static IEnumerable<string> FolderArguments(string? model, IEnumerable<string>? moreFolders = null, string? effort = null)
    {
        var settings = System.Text.Json.JsonSerializer.Serialize(new
        {
            disableAllHooks = true,
            permissions = new { deny = SecretFiles.Select(f => $"Read({f})").ToArray() },
        });
        string[] args = ["-p", "--output-format", "stream-json", "--verbose", "--tools", "Read,Grep,Glob", "--strict-mcp-config",
            "--permission-mode", "default", .. AddDirs(moreFolders), "--settings", settings];
        return [.. args, .. Choice(model, effort)];
    }

    internal static IEnumerable<string> WorkArguments(string? model, IEnumerable<string>? moreFolders = null, string? effort = null)
    {
        var settings = System.Text.Json.JsonSerializer.Serialize(new
        {
            disableAllHooks = true,
            // Edit rules cover every file-editing tool, Write included.
            permissions = new { deny = SecretFiles.SelectMany(f => new[] { $"Read({f})", $"Edit({f})" }).ToArray() },
        });
        string[] args = ["-p", "--output-format", "stream-json", "--verbose", "--tools", "Read,Grep,Glob,Write,Edit", "--strict-mcp-config",
            "--permission-mode", "acceptEdits", .. AddDirs(moreFolders), "--settings", settings];
        return [.. args, .. Choice(model, effort)];
    }

    /// <summary>The model and effort asked for; Claude Code's own defaults when left out.</summary>
    internal static IEnumerable<string> Choice(string? model, string? effort) =>
        [.. model is { Length: > 0 } ? new[] { "--model", model } : [], .. effort is { Length: > 0 } ? new[] { "--effort", effort } : []];

    /// <summary>The folders besides the one Claude Code runs in.</summary>
    private static IEnumerable<string> AddDirs(IEnumerable<string>? folders) => (folders ?? []).SelectMany(f => new[] { "--add-dir", f });

    internal static IEnumerable<string> AskArguments(string? model, string? effort = null)
    {
        string[] args = ["-p", "--output-format", "stream-json", "--verbose", "--tools", "", "--strict-mcp-config",
            "--settings", """{"disableAllHooks":true}"""];
        return [.. args, .. Choice(model, effort)];
    }

    private async Task<string> StartAsync(IReadOnlyList<string> args, string instructions, TimeSpan timeout, CancellationToken ct, string? folder = null)
    {
        var claude = McpClientConfig.FindClaude() ?? throw new ClaudeCodeException("Claude Code is not installed on this PC.");
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
            process = launcher.Start(new ChildProcessStartInfo(commandLine, folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
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
            var (result, failed, calls) = await ReadEventsAsync(process.StandardOutput).ConfigureAwait(false);
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
    /// Reads Claude Code's stream of JSON lines: its tool calls are counted; the last line is the result (its text, and
    /// whether it is an error).
    /// </summary>
    internal static async Task<(string? Result, bool Failed, int Calls)> ReadEventsAsync(Stream output)
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
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use") calls++;
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
