using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai.Assistant;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.App.Ai;

/// <summary>
/// OpenAI's Codex CLI (a ChatGPT plan, signed in for Helm in its own folder: CODEX_HOME) as the brain of Helm's agent
/// jobs on this PC. Each turn is one <c>codex exec --json</c> run in an empty folder with the read-only sandbox; later
/// turns resume the same session. Helm lists its tools in the first message and runs the calls Codex asks for.
/// </summary>
internal sealed class CodexAgentFactory(IChildProcessLauncher launcher, string home, ILogger logger) : IAssistantChatFactory
{
    public const string HomeVariable = "CODEX_HOME";

    private string WorkFolder => Path.Combine(home, "work");

    private DateTimeOffset _limitedUntil;

    public Helm.Ai.AiProvider Provider => Helm.Ai.AiProviders.Claude;

    /// <summary>Why it takes no job now (not installed, not signed in here, at its limit), or null.</summary>
    public string? Unavailable()
    {
        if (FindCodex() is null) return "Codex is not installed on this PC (npm install -g @openai/codex).";
        if (!SignedIn(home)) return "not signed in: press Sign in.";
        return DateTimeOffset.UtcNow < _limitedUntil ? $"reached its plan's limit; Helm tries it again at {_limitedUntil.ToLocalTime():HH:mm}." : null;
    }

    /// <summary>codex login writes auth.json to CODEX_HOME.</summary>
    internal static bool SignedIn(string home) => File.Exists(Path.Combine(home, "auth.json"));

    internal void ReachedLimit()
    {
        _limitedUntil = DateTimeOffset.UtcNow + ClaudeCodeAgentFactory.RestAfterLimit;
        logger.LogWarning("Codex reached its usage limit; agent jobs go elsewhere until {Until}", _limitedUntil);
    }

    public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
    {
        Directory.CreateDirectory(WorkFolder);
        return new CodexChat(this, launcher, logger, home, WorkFolder, system, tools);
    }

    /// <summary>codex on the PATH (npm puts codex.cmd in %APPDATA%\npm).</summary>
    internal static string? FindCodex()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var folder in folders)
            foreach (var name in new[] { "codex.cmd", "codex.exe" })
            {
                var path = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(path)) return path;
            }
        return null;
    }
}

internal sealed class CodexChat(CodexAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string workFolder,
    string system, IReadOnlyList<AssistantTool> tools) : IAssistantChat
{
    private string _pending = "";
    private bool _first = true;
    private string? _thread;
    private int _calls;

    public void AddUser(string text)
    {
        _pending = _first ? $"{CliAgentProtocol.Preamble(system, tools)}\n\n{text}" : text;
        _first = false;
    }

    public void AddResults(IReadOnlyList<ToolOutcome> results) => _pending = CliAgentProtocol.Results(results);

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        var codex = CodexAgentFactory.FindCodex() ?? throw new AssistantException("Codex is not installed on this PC.");
        // Read-only sandbox in an empty folder: Codex has nothing to look at or change; Helm runs the tools.
        string[] options = ["exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only", "--cd", workFolder];
        string[] args = _thread is { } thread ? [.. options, "resume", thread, "-"] : [.. options, "-"];
        var commandLine = codex.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"{CommandLine.Quote(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([codex, .. args])}\""
            : CommandLine.Join([codex, .. args]);
        ChildProcess process;
        try
        {
            process = launcher.Start(new ChildProcessStartInfo(commandLine, workFolder)
            {
                Environment = ClaudeCodeAgentFactory.Environment(CodexAgentFactory.HomeVariable, home),
            });
        }
        catch (ChildProcessException ex)
        {
            throw new AssistantException("Codex could not be started: " + ex.Message, ex);
        }
        using (process)
        using (ct.Register(() => { try { process.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }))
        {
            await using (var input = new StreamWriter(process.StandardInput, new UTF8Encoding(false)))
                await input.WriteAsync(_pending.AsMemory(), ct);
            _ = Drain(process.StandardError);
            using var output = new StreamReader(process.StandardOutput, Encoding.UTF8);
            string? answer = null, error = null;
            long input2 = 0, cached = 0, outputTokens = 0;
            while (await output.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                JsonNode? ev;
                try
                {
                    ev = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }
                var type = ev?["type"]?.GetValue<string>();
                var item = ev?["item"];
                if (type == "item.completed" && item?["type"]?.GetValue<string>() == "agent_message") type = "agent_message";
                switch (type)
                {
                    case "thread.started":
                        _thread ??= ev!["thread_id"]?.GetValue<string>();
                        break;
                    case "agent_message":
                        answer = item!["text"]?.GetValue<string>();
                        break;
                    case "turn.completed":
                        var usage = ev!["usage"];
                        long Read(string name) => usage?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
                        input2 = Read("input_tokens");
                        cached = Read("cached_input_tokens");
                        outputTokens = Read("output_tokens") + Read("reasoning_output_tokens");
                        break;
                    case "turn.failed":
                        error = ev!["error"]?["message"]?.GetValue<string>() ?? "the turn failed";
                        break;
                    case "error":
                        error = ev!["message"]?.GetValue<string>() ?? "it stopped with an error";
                        break;
                }
            }
            await process.Exited;
            ct.ThrowIfCancellationRequested();
            if (error is not null && answer is null)
            {
                if (error.Contains("limit", StringComparison.OrdinalIgnoreCase)) factory.ReachedLimit();
                logger.LogWarning("Codex failed: {Error}", error);
                throw new AssistantException($"Codex: {error}");
            }
            var step = new AssistantUsage(Math.Max(0, input2 - cached), outputTokens, cached, 0);
            return CliAgentProtocol.Parse(answer ?? "", step, ref _calls);
        }
    }

    private static async Task Drain(Stream errors)
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
}
