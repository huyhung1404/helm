using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai.Assistant;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.App.Ai;

/// <summary>
/// xAI's Grok CLI (Grok Build, on a SuperGrok or X Premium+ plan, signed in for Helm in its own folder: GROK_HOME) as the
/// brain of Helm's agent jobs on this PC. One <c>grok agent stdio</c> process per conversation speaks the Agent Client
/// Protocol (JSON-RPC over stdin/stdout); it works in an empty folder, every permission it asks for is refused, and
/// Helm lists its tools in the first message and runs the calls Grok asks for.
/// </summary>
internal sealed class GrokAgentFactory(IChildProcessLauncher launcher, string home, ILogger logger) : IAssistantChatFactory
{
    public const string HomeVariable = "GROK_HOME";

    /// <summary>Written by Helm's sign-in terminal once <c>grok login</c> succeeded (the CLI's own token file is not documented).</summary>
    internal const string SignedInMarker = ".helm-signed-in";

    private string WorkFolder => Path.Combine(home, "work");

    private DateTimeOffset _limitedUntil;

    public Helm.Ai.AiProvider Provider => Helm.Ai.AiProviders.Grok;

    /// <summary>Why it takes no job now (not installed, not signed in here, at its limit), or null.</summary>
    public string? Unavailable()
    {
        if (FindGrok() is null) return "Grok CLI is not installed on this PC (npm install -g @xai-official/grok).";
        if (!SignedIn(home)) return "not signed in: press Sign in.";
        return DateTimeOffset.UtcNow < _limitedUntil ? $"reached its plan's limit; Helm tries it again at {_limitedUntil.ToLocalTime():HH:mm}." : null;
    }

    internal static bool SignedIn(string home) => File.Exists(Path.Combine(home, SignedInMarker));

    internal void ReachedLimit()
    {
        _limitedUntil = DateTimeOffset.UtcNow + ClaudeCodeAgentFactory.RestAfterLimit;
        logger.LogWarning("Grok reached its usage limit; agent jobs go elsewhere until {Until}", _limitedUntil);
    }

    /// <summary>A sign-in that no longer works (the token expired or was revoked): Sign in again.</summary>
    internal void SignedOut()
    {
        try
        {
            File.Delete(Path.Combine(home, SignedInMarker));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
    {
        Directory.CreateDirectory(WorkFolder);
        return new GrokChat(this, launcher, logger, home, WorkFolder, system, tools);
    }

    /// <summary>
    /// The native grok.exe (its installer and npm's first run put it in ~/.grok/bin), else npm's grok.cmd. Helm runs the
    /// native one with its own GROK_HOME, so npm's launcher does not unpack another copy into every agent's folder.
    /// </summary>
    internal static string? FindGrok()
    {
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "bin", "grok.exe");
        if (File.Exists(native)) return native;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var folder in folders)
            foreach (var name in new[] { "grok.exe", "grok.cmd" })
            {
                var path = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(path)) return path;
            }
        return null;
    }
}

/// <summary>One conversation with one <c>grok agent stdio</c> process.</summary>
internal sealed class GrokChat(GrokAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string workFolder,
    string system, IReadOnlyList<AssistantTool> tools) : IAssistantChat, IDisposable
{
    private ChildProcess? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private string? _session;
    private int _nextId;
    private string _pending = "";
    private bool _first = true;
    private int _calls;

    public void AddUser(string text)
    {
        _pending = _first ? $"{CliAgentProtocol.Preamble(system, tools)}\n\n{text}" : text;
        _first = false;
    }

    public void AddResults(IReadOnlyList<ToolOutcome> results) => _pending = CliAgentProtocol.Results(results);

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        using var stop = ct.Register(Kill);
        try
        {
            if (_session is null) await StartAsync(ct);
            var answer = new StringBuilder();
            var prompt = new JsonObject
            {
                ["sessionId"] = _session,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = _pending }),
            };
            await RequestAsync("session/prompt", prompt, answer, ct);
            // The plan's usage is not reported: estimated from the text (about 4 characters a token), for what it saved.
            var step = new AssistantUsage(_pending.Length / 4, answer.Length / 4, 0, 0);
            return CliAgentProtocol.Parse(answer.ToString(), step, ref _calls);
        }
        catch (IOException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public void Dispose() => Kill();

    private async Task StartAsync(CancellationToken ct)
    {
        var grok = GrokAgentFactory.FindGrok() ?? throw new AssistantException("Grok CLI is not installed on this PC.");
        string[] args = ["agent", "stdio"];
        var commandLine = grok.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"{CommandLine.Quote(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([grok, .. args])}\""
            : CommandLine.Join([grok, .. args]);
        try
        {
            _process = launcher.Start(new ChildProcessStartInfo(commandLine, workFolder)
            {
                Environment = ClaudeCodeAgentFactory.Environment(GrokAgentFactory.HomeVariable, home),
            });
        }
        catch (ChildProcessException ex)
        {
            throw new AssistantException("Grok could not be started: " + ex.Message, ex);
        }
        _input = new StreamWriter(_process.StandardInput, new UTF8Encoding(false));
        _output = new StreamReader(_process.StandardOutput, Encoding.UTF8);
        _ = DrainAsync(_process.StandardError);

        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            // Grok gets no files and no terminal from Helm: Helm's tools are its only way to act.
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
        }, null, ct);
        try
        {
            await RequestAsync("authenticate", new JsonObject { ["methodId"] = "cached_token" }, null, ct);
        }
        catch (AssistantException ex)
        {
            factory.SignedOut();
            throw new AssistantException("Grok is not signed in any more: press Sign in on AI → Proxy. " + ex.Message, ex);
        }
        var session = await RequestAsync("session/new", new JsonObject { ["cwd"] = workFolder, ["mcpServers"] = new JsonArray() }, null, ct);
        _session = session?["sessionId"]?.GetValue<string>() ?? throw new AssistantException("Grok started no session.");
        logger.LogInformation("Grok started for an agent job");
    }

    /// <summary>
    /// Sends a request and reads until its response, collecting Grok's message text on the way and refusing what Grok
    /// asks of Helm (permissions, files, a terminal).
    /// </summary>
    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, StringBuilder? answer, CancellationToken ct)
    {
        var id = ++_nextId;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, ct);
        while (await _output!.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (message is null) continue;
            var asked = message["method"]?.GetValue<string>();
            if (asked is null)
            {
                if (message["id"] is not JsonValue got || !got.TryGetValue<int>(out var gotId) || gotId != id) continue;
                if (message["error"] is { } error)
                {
                    var text = error["message"]?.GetValue<string>() ?? "it stopped with an error";
                    if (text.Contains("limit", StringComparison.OrdinalIgnoreCase) || text.Contains("quota", StringComparison.OrdinalIgnoreCase))
                        factory.ReachedLimit();
                    logger.LogWarning("Grok {Method} failed: {Error}", method, text);
                    throw new AssistantException($"Grok: {text}");
                }
                return message["result"];
            }
            if (message["id"] is { } requestId)
            {
                // Grok asks Helm for something: a permission is cancelled, anything else is not offered.
                var reply = asked == "session/request_permission"
                    ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["result"] = new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } } }
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Helm does not offer this." } };
                await SendAsync(reply, ct);
                continue;
            }
            if (asked == "session/update" && answer is not null
                && message["params"]?["update"] is { } update
                && update["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk"
                && update["content"]?["text"]?.GetValue<string>() is { } chunk)
                answer.Append(chunk);
        }
        ct.ThrowIfCancellationRequested();
        throw new AssistantException("Grok stopped before it answered.");
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await _input!.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
        await _input.FlushAsync(ct);
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
        _session = null;
    }
}
