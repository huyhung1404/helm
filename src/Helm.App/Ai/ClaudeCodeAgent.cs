using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.App.Ai;

/// <summary>
/// Claude Code (the official CLI) as the brain of Helm's agent jobs on this PC, signed in to the account the user chose
/// for it in its own folder (CLAUDE_CONFIG_DIR), never the one their own Claude Code uses. It runs with no tools of its
/// own, no MCP server and no hooks: Helm lists its tools in the conversation, Claude answers with the calls it wants as
/// JSON, Helm runs them and sends the results back, as on the API.
/// </summary>
internal sealed class ClaudeCodeAgentFactory(IChildProcessLauncher launcher, string home, ILogger logger) : IAssistantChatFactory
{
    public const string HomeVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>After Claude Code says its plan's limit is reached, it gets no job for this long (the API takes over).</summary>
    internal static readonly TimeSpan RestAfterLimit = TimeSpan.FromMinutes(30);

    private DateTimeOffset _limitedUntil;

    public AiProvider Provider => AiProviders.Claude;

    /// <summary>Why it takes no job now (not installed, not signed in here, at its limit), or null.</summary>
    public string? Unavailable() =>
        McpClientConfig.FindClaude() is null ? "Claude Code is not installed on this PC."
        : !SignedIn(home) ? "not signed in: press Sign in."
        : DateTimeOffset.UtcNow < _limitedUntil ? $"reached its plan's limit; Helm tries it again at {_limitedUntil.ToLocalTime():HH:mm}."
        : null;

    /// <summary>Claude Code keeps its sign-in in .credentials.json in its config folder.</summary>
    internal static bool SignedIn(string home) => File.Exists(Path.Combine(home, ".credentials.json"));

    /// <summary>The child's environment: Helm's, with this agent's own config folder.</summary>
    internal static IReadOnlyDictionary<string, string> Environment(string variable, string home)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        env[variable] = home;
        return env;
    }

    internal void ReachedLimit()
    {
        _limitedUntil = DateTimeOffset.UtcNow + RestAfterLimit;
        logger.LogWarning("Claude Code reached its usage limit; agent jobs go to the API until {Until}", _limitedUntil);
    }

    public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools) =>
        // A Claude model picked for the API runs here too; any other pick (Grok) runs on the plan's Haiku.
        new ClaudeCodeChat(this, launcher, logger, home, model.StartsWith("claude-", StringComparison.Ordinal) ? model : "haiku", effort, system, tools);
}

/// <summary>One conversation with one Claude Code process (stream-json in and out: each turn takes about a second).</summary>
internal sealed class ClaudeCodeChat(ClaudeCodeAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string model,
    string effort, string system, IReadOnlyList<AssistantTool> tools) : IAssistantChat, IDisposable
{
    private ChildProcess? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private string _pending = "";
    private string _more = "";
    private bool _first = true;
    private int _calls;

    public void AddUser(string text)
    {
        _pending = _first ? $"{CliAgentProtocol.Preamble(system, tools)}\n\n{text}" : (_pending.Length > 0 ? _pending + "\n\n" : "") + _more + text;
        _first = false;
        _more = "";
    }

    public void AddTools(IReadOnlyList<AssistantTool> added) => _more = CliAgentProtocol.MoreTools(added) + "\n";

    public void AddResults(IReadOnlyList<ToolOutcome> results) => _pending = CliAgentProtocol.Results(results);

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        Start();
        using var stop = ct.Register(Kill);
        var line = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = _pending },
        };
        _pending = "";
        await _input!.WriteLineAsync(line.ToJsonString().AsMemory(), ct);
        await _input.FlushAsync(ct);

        while (await _output!.ReadLineAsync(ct) is { } text)
        {
            if (text.Length == 0 || text[0] != '{') continue;
            JsonNode? ev;
            try
            {
                ev = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }
            if (ev?["type"]?.GetValue<string>() != "result") continue;
            var answer = ev["result"]?.GetValue<string>() ?? "";
            var usage = ev["usage"];
            long Read(string name) => usage?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            var step = new AssistantUsage(Read("input_tokens"), Read("output_tokens"), Read("cache_read_input_tokens"), Read("cache_creation_input_tokens"));
            if (ev["is_error"] is JsonValue e && e.TryGetValue<bool>(out var failed) && failed)
            {
                if (answer.Contains("limit", StringComparison.OrdinalIgnoreCase)) factory.ReachedLimit();
                throw new AssistantException($"Claude Code: {(answer.Length > 0 ? answer : "it stopped with an error")}");
            }
            return CliAgentProtocol.Parse(answer, step, ref _calls);
        }
        ct.ThrowIfCancellationRequested();
        throw new AssistantException("Claude Code stopped before it answered.");
    }

    public void Dispose() => Kill();

    private void Start()
    {
        if (_process is not null) return;
        var claude = McpClientConfig.FindClaude() ?? throw new AssistantException("Claude Code is not installed on this PC.");
        string[] args = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--tools", "", "--strict-mcp-config",
            "--settings", """{"disableAllHooks":true}""", "--model", model, .. effort is { Length: > 0 } ? new[] { "--effort", effort } : []];
        var commandLine = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            // npm's claude.cmd only runs through cmd.exe; these arguments hold nothing cmd.exe reads itself.
            ? $"{CommandLine.Quote(System.Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([claude, .. args])}\""
            : CommandLine.Join([claude, .. args]);
        try
        {
            Directory.CreateDirectory(home);
            _process = launcher.Start(new ChildProcessStartInfo(commandLine, home)
            {
                Environment = ClaudeCodeAgentFactory.Environment(ClaudeCodeAgentFactory.HomeVariable, home),
            });
        }
        catch (ChildProcessException ex)
        {
            throw new AssistantException("Claude Code could not be started: " + ex.Message, ex);
        }
        _input = new StreamWriter(_process.StandardInput, new UTF8Encoding(false));
        _output = new StreamReader(_process.StandardOutput, Encoding.UTF8);
        _ = DrainAsync(_process.StandardError);
        logger.LogInformation("Claude Code started for an agent job ({Model})", model);
    }

    private static async Task DrainAsync(Stream errors)
    {
        using var reader = new StreamReader(errors, Encoding.UTF8);
        try
        {
            while (await reader.ReadLineAsync() is not null) { }
        }
        catch (IOException)
        {
        }
    }

    private void Kill()
    {
        try
        {
            _process?.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _process?.Dispose();
        _process = null;
    }
}
