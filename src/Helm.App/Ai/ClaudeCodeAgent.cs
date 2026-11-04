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

    /// <summary>Photos go as image blocks of its stream-json user message (<see cref="CliImages.ClaudeCodeContent"/>).</summary>
    public bool ReadsImages => true;

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

/// <summary>
/// One conversation with one Claude Code process (stream-json in and out: each turn takes about a second). A process
/// that died or was stopped between turns is started again, and told the whole protocol and what was said so far.
/// </summary>
internal sealed class ClaudeCodeChat(ClaudeCodeAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string model,
    string effort, string system, IReadOnlyList<AssistantTool> tools) : IAssistantChat, IDisposable
{
    private readonly CliConversation _conversation = new(system, tools);
    private ChildProcess? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private int _calls;

    /// <summary>The running process got its first message (the preamble); one started ahead of the turn has not yet.</summary>
    private bool _greeted;

    public void AddUser(string text) => _conversation.AddUser(text);

    public void AddUser(string text, AssistantImage image) => _conversation.AddUser(text, image);

    public void AddTools(IReadOnlyList<AssistantTool> added) => _conversation.AddTools(added);

    public void AddResults(IReadOnlyList<ToolOutcome> results) => _conversation.AddResults(results);

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        using var stop = ct.Register(Kill);
        await SendAsync(ct);
        var output = _output!;
        while (await ReadLineAsync(output, ct) is { } text)
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
            _conversation.Answered(answer);
            return CliAgentProtocol.Parse(answer, step, ref _calls);
        }
        ct.ThrowIfCancellationRequested();
        // It died mid-turn: what was sent waits for the next turn, which starts a new process.
        Kill();
        throw new AssistantException("Claude Code stopped before it answered.");
    }

    /// <summary>
    /// Starts the process ahead of the first turn (the card opened): Claude Code loads while the user speaks. Nothing
    /// is sent: stream-json has no message that does not call the model, so the preamble goes with the first turn.
    /// </summary>
    public Task WarmAsync(CancellationToken ct)
    {
        if (_process is { HasExited: true }) Kill();
        if (_process is null) Start();
        return Task.CompletedTask;
    }

    public void Dispose() => Kill();

    /// <summary>
    /// Sends what waits, to the running process or (when there is none, or it died since the last turn and writing
    /// to it fails) to a new one, which gets the preamble and the recap first.
    /// </summary>
    private async Task SendAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (_process is { HasExited: true }) Kill();
            if (_process is null) Start();
            var fresh = !_greeted;
            var line = new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = CliImages.ClaudeCodeContent(_conversation.Message(fresh), _conversation.Images) },
            };
            try
            {
                await _input!.WriteLineAsync(line.ToJsonString().AsMemory(), ct);
                await _input.FlushAsync(ct);
                _greeted = true;
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException && !ct.IsCancellationRequested)
            {
                Kill();
                if (attempt > 0) throw new AssistantException("Claude Code stopped before it answered.", ex);
                logger.LogInformation("Claude Code's process had ended; Helm starts a new one for the conversation");
            }
        }
    }

    /// <summary>A line of the answer; a stream closed under it (the process killed or gone) ends it.</summary>
    private static async Task<string?> ReadLineAsync(StreamReader output, CancellationToken ct)
    {
        try
        {
            return await output.ReadLineAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void Start()
    {
        var claude = McpClientConfig.FindClaude() ?? throw new AssistantException("Claude Code is not installed on this PC.");
        // No session file: Helm keeps the conversation itself (a new process gets a recap), and a photo never lands on disk.
        string[] args = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--tools", "", "--strict-mcp-config",
            "--no-session-persistence",
            "--settings", """{"disableAllHooks":true}""", "--model", model, .. Helm.Ai.AiEfforts.Nearest(effort, Helm.Ai.AiEfforts.For(model, "claude-code")) is { Length: > 0 } level ? new[] { "--effort", level } : []];
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void Kill()
    {
        _greeted = false;
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        process.Dispose();
    }
}
