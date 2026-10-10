using System.IO;
using System.Text;
using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Helm.Modules.NovelReader.Names;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The AI name scan on Windows: Claude Code (the user's own Claude plan, no API key) run without a window
/// (<c>claude -p</c>), allowed only Novel Reader's MCP tools. It reads the candidates through Helm's MCP server, which runs
/// inside this Helm, and adds the names it judges real with <c>novel_add_names</c>, so they arrive in the library while it
/// works. Needs Claude Code installed and Helm added to it (the AI &amp; MCP page). The instructions go in on standard
/// input, never through cmd.exe; it runs with the user's normal rights even when Helm is elevated, and stops with Helm.
/// </summary>
public sealed class ClaudeCodeNameAgent(McpClientConfig mcp, IChildProcessLauncher launcher, ILogger<ClaudeCodeNameAgent> logger) : INameScanAgent
{
    /// <summary>A long novel has a few hundred candidates; far more than this means it is stuck.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>The only tools it may call (MCP tools are named mcp__&lt;server&gt;__&lt;tool&gt; in Claude Code).</summary>
    public static IReadOnlyList<string> AllowedTools { get; } =
        new[] { "novel_list", "novel_name_candidates", "novel_names", "novel_add_names", "novel_ignore_names" }
            .Select(t => $"mcp__{McpClientConfig.ServerName}__{t}").ToList();

    public string Name => "Claude Code";

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

    /// <summary>What Claude Code is asked to do (in English, the language it works best in).</summary>
    public static string Instructions(string bookId, string title) =>
        $"""
        Find the character names of the novel "{title}" (id {bookId}) in Helm's Novel Reader, using only Helm's novel tools.
        The reader reads Chinese web novels through a Vietnamese QuickTranslator converter, which does not know this
        novel's names, so they come out as ordinary words.

        1. Call novel_name_candidates with novel "{bookId}" and offset 0, then keep calling it with the returned
           next_offset until next_offset is null. Each candidate has its count, Hán Việt reading, what Helm's logic scan
           thinks, whether it is saved already, and sentences where it is used.
        2. Judge every candidate that is not saved or set aside from its sentences, not from its characters alone:
           - a name is a person (full name, given name, courtesy name, nickname used as a name), a named place, or a named
             sect, clan or house (林府, 洛家). A given name that also appears inside a full name (景桓 of 洛景桓) is still a
             name when the novel uses it alone: add it too;
           - not a name: a piece of a longer word or idiom (成怒 of 恼羞成怒), a name with a word stuck to it (向林宛,
             谢珩低), a title or form of address (侯爷, 皇上), an ordinary word.
        3. After each page, call novel_add_names with the names of that page, written as a Vietnamese reader writes them:
           the Hán Việt reading, every syllable capitalised for a person or a place (林宛 → Lâm Uyển); a house or family
           word stays lowercase (林府 → Lâm phủ, 洛家 → Lạc gia). Then call novel_ignore_names with the words of that page
           that are clearly not names (later scans skip them for good, so never a possible name). Leave out words you are
           unsure of.
        4. Never change or remove names that are saved already.

        When every page is done, answer with one short line: how many names you added and how many words you set aside.
        """;

    public async Task<string> FindNamesAsync(string bookId, string title, IProgress<string>? progress, CancellationToken ct)
    {
        if (Unavailable() is { } problem) throw new NameAgentException(problem);
        var claude = McpClientConfig.FindClaude()!;
        IReadOnlyList<string> args = ["-p", "--output-format", "stream-json", "--verbose", "--allowedTools", string.Join(",", AllowedTools)];
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
            throw new NameAgentException("Claude Code could not be started: " + ex.Message, ex);
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var stop = timeout.Token.Register(() =>
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
                await input.WriteAsync(Instructions(bookId, title)).ConfigureAwait(false);
            var errors = ReadAllAsync(process.StandardError);
            var (result, failed, calls) = await ReadEventsAsync(process.StandardOutput, progress).ConfigureAwait(false);
            var exitCode = await process.Exited.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new NameAgentException($"Claude Code did not finish within {Timeout.TotalMinutes:0} minutes.");
            logger.LogInformation("Claude Code looked for names with {Calls} tool calls and exited with {ExitCode}", calls, exitCode);
            if (failed || exitCode != 0)
            {
                var why = (result ?? await errors.ConfigureAwait(false)).Trim();
                throw new NameAgentException("Claude Code stopped: " + (why.Length > 0 ? why[..Math.Min(why.Length, 300)] : $"exit code {exitCode}."));
            }
            return result ?? "";
        }
    }

    /// <summary>
    /// Reads Claude Code's stream of JSON lines: each tool call moves the progress on; the last line is the result
    /// (its text, and whether it is an error).
    /// </summary>
    internal static async Task<(string? Result, bool Failed, int Calls)> ReadEventsAsync(Stream output, IProgress<string>? progress)
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
                        progress?.Report(tool switch
                        {
                            "novel_name_candidates" => $"Claude Code is reading the possible names… ({calls} steps)",
                            "novel_add_names" => $"Claude Code is adding names… ({calls} steps)",
                            "novel_ignore_names" => $"Claude Code is setting aside words that are not names… ({calls} steps)",
                            _ => $"Claude Code is working… ({calls} steps)",
                        });
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
