using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai.Assistant;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.App.Ai;

/// <summary>
/// OpenAI's Codex CLI (a ChatGPT plan, signed in for Helm in its own folder: CODEX_HOME) as the brain of Helm's agent
/// jobs on this PC: a conversation keeps one <c>codex app-server</c> running (see <see cref="CodexChat"/>), in an empty
/// folder with the read-only sandbox and none of Codex's own tools. Helm lists its tools in the first message and runs
/// the calls Codex asks for.
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

    /// <summary>
    /// Codex's own tools, turned off (codex 0.166 feature names; an unknown one stops codex with an error, so a later
    /// codex that drops one shows here at once). What no switch takes away (<c>codex debug prompt-input</c>): its
    /// JavaScript <c>exec</c>, which only calls the tools left, its question to the user, and its sub-agents.
    /// </summary>
    internal static readonly string[] OwnTools =
    [
        "shell_tool", "unified_exec", "web_search_request", "web_search_cached", "standalone_web_search", "view_image",
        "image_generation", "browser_use", "browser_use_external", "computer_use", "in_app_browser", "apps", "plugins",
        "request_permissions_tool", "search_tool", "tool_search", "sleep_tool",
    ];

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

    /// <summary>
    /// How every Codex run is set up, the app server's and <c>codex exec</c>'s alike: none of its own tools (an unknown
    /// feature stops codex with an error), no web search (on by default since the web_search_* features went), and one
    /// line of instructions of its own instead of its 22 KB coding prompt, skills list and sandbox notes (checked with
    /// <c>codex debug prompt-input</c>: 60 KB of prompt became 22 KB). Helm's instructions come in the first message.
    /// </summary>
    internal static IEnumerable<string> Settings() =>
    [
        .. OwnTools.SelectMany(f => new[] { "--disable", f }),
        "-c", "web_search=\"disabled\"",
        "-c", "instructions=\"You are Helm's assistant. Follow the instructions in the user's first message exactly.\"",
        "-c", "skills.bundled.enabled=false",
        "-c", "skills.include_instructions=false",
        "-c", "include_permissions_instructions=false",
        "-c", "include_apps_instructions=false",
        "-c", "include_collaboration_mode_instructions=false",
        "-c", "analytics.enabled=false",
    ];

    /// <summary>
    /// The app server, on stdio. Unlike <c>codex exec</c> it has no <c>--ignore-user-config</c>: it only runs while
    /// CODEX_HOME (Helm's own folder) has no config.toml (<see cref="AppServerAllowed"/>), else every turn is a
    /// <c>codex exec</c> that ignores it.
    /// </summary>
    internal static string[] AppServerArguments() =>
        ["app-server", .. Settings(), "-c", "sandbox_mode=\"read-only\"", "-c", "approval_policy=\"never\""];

    internal static bool AppServerAllowed(string home) => !File.Exists(Path.Combine(home, "config.toml"));

    /// <summary>One <c>codex exec</c> turn: a new session, or the next turn of <paramref name="thread"/>.</summary>
    internal static string[] ExecArguments(string workFolder, string? thread)
    {
        // None of Codex's own tools: read-only only stops writes, its shell could still read the user's files
        // (~/.claude.json, ~/.ssh, Helm's data). In an empty folder, with Helm running the tools.
        string[] options = ["exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only", "--ignore-user-config", .. Settings(), "--cd", workFolder];
        return thread is { } id ? [.. options, "resume", id, "-"] : [.. options, "-"];
    }

    /// <summary>The command line for <paramref name="codex"/>: npm's codex.cmd only runs through cmd.exe.</summary>
    internal static string CommandLineFor(string codex, IEnumerable<string> args) =>
        codex.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"{CommandLine.Quote(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([codex, .. args])}\""
            : CommandLine.Join([codex, .. args]);

    /// <summary>
    /// The child's environment: Helm's with CODEX_HOME, and for npm's native codex.exe what npm's launcher (codex.js)
    /// would have added.
    /// </summary>
    internal IReadOnlyDictionary<string, string> ChildEnvironment(string codex)
    {
        var env = new Dictionary<string, string>(ClaudeCodeAgentFactory.Environment(HomeVariable, home), StringComparer.OrdinalIgnoreCase);
        if (NpmPackageOf(codex) is { } package)
        {
            env["CODEX_MANAGED_PACKAGE_ROOT"] = package;
            env["CODEX_MANAGED_BY_NPM"] = "1";
        }
        return env;
    }

    /// <summary>
    /// codex on the PATH: npm's native codex.exe when npm installed it (npm's codex.cmd starts cmd.exe and node only to
    /// start that exe: 70-100 ms a run), else codex.cmd, or a codex.exe of its own.
    /// </summary>
    internal static string? FindCodex()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var folder in folders.Select(f => f.Trim('"')))
        {
            var cmd = Path.Combine(folder, "codex.cmd");
            if (File.Exists(cmd)) return File.Exists(NativeOf(folder)) ? NativeOf(folder) : cmd;
            var exe = Path.Combine(folder, "codex.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Where npm's @openai/codex keeps the native codex.exe for this PC, beside its codex.cmd in <paramref name="npmFolder"/>.</summary>
    internal static string NativeOf(string npmFolder)
    {
        var (platform, triple) = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? ("codex-win32-arm64", "aarch64-pc-windows-msvc")
            : ("codex-win32-x64", "x86_64-pc-windows-msvc");
        return Path.Combine(npmFolder, "node_modules", "@openai", "codex", "node_modules", "@openai", platform, "vendor", triple, "bin", "codex.exe");
    }

    /// <summary>The @openai/codex package folder an npm codex.exe belongs to, or null for a codex.exe of its own.</summary>
    private static string? NpmPackageOf(string codex)
    {
        var marker = Path.DirectorySeparatorChar + Path.Combine("node_modules", "@openai", "codex") + Path.DirectorySeparatorChar;
        var at = codex.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : codex[..(at + marker.Length - 1)];
    }
}

/// <summary>
/// One conversation with Codex: one <c>codex app-server</c> process whose thread stays loaded between turns (a turn
/// costs the model's time, not a process start, a sign-in check and a session reload). One that died or was stopped is
/// started again, and told the whole protocol and what was said so far. When the app server cannot be used (an older
/// codex, a config.toml in CODEX_HOME), each turn is one <c>codex exec --json</c> run that resumes the session of the first.
/// </summary>
internal sealed class CodexChat(CodexAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string workFolder,
    string system, IReadOnlyList<AssistantTool> tools) : IAssistantChat, IDisposable
{
    private readonly CliConversation _conversation = new(system, tools);
    private ChildProcess? _process;
    private CodexAppServer? _server;
    private bool _execOnly = !CodexAgentFactory.AppServerAllowed(home);
    private string? _thread;
    private int _calls;

    public void AddUser(string text) => _conversation.AddUser(text);

    public void AddTools(IReadOnlyList<AssistantTool> added) => _conversation.AddTools(added);

    public void AddResults(IReadOnlyList<ToolOutcome> results) => _conversation.AddResults(results);

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        var codex = CodexAgentFactory.FindCodex() ?? throw new AssistantException("Codex is not installed on this PC.");
        var (answer, usage) = (_execOnly ? null : await ServerTurnAsync(codex, ct)) ?? await ExecAsync(codex, ct);
        _conversation.Answered(answer);
        return CliAgentProtocol.Parse(answer, usage, ref _calls);
    }

    public void Dispose() => Kill();

    /// <summary>The turn on the app server (started when there is none or it ended), or null when it cannot start.</summary>
    private async Task<(string Answer, AssistantUsage Usage)?> ServerTurnAsync(string codex, CancellationToken ct)
    {
        using var stop = ct.Register(Kill);
        if (_process is { HasExited: true }) Kill();
        var fresh = _server is null;
        if (fresh && !await StartServerAsync(codex, ct)) return null;
        try
        {
            return await _server!.TurnAsync(_conversation.Message(fresh), ct);
        }
        catch (AssistantException ex)
        {
            if (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)) factory.ReachedLimit();
            logger.LogWarning("Codex failed: {Error}", ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // It died mid-turn (or was stopped): what was sent waits for the next turn, which starts a new process.
            Kill();
            ct.ThrowIfCancellationRequested();
            throw new AssistantException("Codex stopped before it answered.", ex);
        }
    }

    private async Task<bool> StartServerAsync(string codex, CancellationToken ct)
    {
        ChildProcess process;
        try
        {
            process = launcher.Start(new ChildProcessStartInfo(CodexAgentFactory.CommandLineFor(codex, CodexAgentFactory.AppServerArguments()), workFolder)
            {
                Environment = factory.ChildEnvironment(codex),
            });
        }
        catch (ChildProcessException ex)
        {
            throw new AssistantException("Codex could not be started: " + ex.Message, ex);
        }
        _process = process;
        _ = Drain(process.StandardError);
        var server = new CodexAppServer(new StreamReader(process.StandardOutput, Encoding.UTF8), new StreamWriter(process.StandardInput, new UTF8Encoding(false)));
        try
        {
            await server.StartAsync(workFolder, typeof(CodexChat).Assembly.GetName().Version?.ToString(3) ?? "0", ct);
            _server = server;
            logger.LogInformation("Codex app server started for an agent job");
            return true;
        }
        catch (Exception ex) when (ex is AssistantException or IOException or ObjectDisposedException && !ct.IsCancellationRequested)
        {
            Kill();
            _execOnly = true;
            logger.LogWarning("Codex's app server did not start ({Error}); this conversation runs one codex exec a turn", ex.Message);
            return false;
        }
    }

    private async Task<(string Answer, AssistantUsage Usage)> ExecAsync(string codex, CancellationToken ct)
    {
        var fresh = _thread is null;
        ChildProcess process;
        try
        {
            process = launcher.Start(new ChildProcessStartInfo(CodexAgentFactory.CommandLineFor(codex, CodexAgentFactory.ExecArguments(workFolder, _thread)), workFolder)
            {
                Environment = factory.ChildEnvironment(codex),
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
                await input.WriteAsync(_conversation.Message(fresh).AsMemory(), ct);
            _ = Drain(process.StandardError);
            using var output = new StreamReader(process.StandardOutput, Encoding.UTF8);
            string? answer = null, error = null, thread = null;
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
                        thread = ev!["thread_id"]?.GetValue<string>();
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
            // The session is kept once a turn went through: a failed first turn starts over with the preamble.
            _thread ??= thread;
            return (answer ?? "", new AssistantUsage(Math.Max(0, input2 - cached), outputTokens, cached, 0));
        }
    }

    private void Kill()
    {
        _server = null;
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

    private static async Task Drain(Stream errors)
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
}
