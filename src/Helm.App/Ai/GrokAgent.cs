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
/// brain of Helm's agent jobs on this PC. Each turn is one headless run with none of Grok's own tools (see
/// <see cref="GrokChat"/>); Helm lists its tools in the first message and runs the calls Grok asks for.
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

    /// <summary>
    /// Every built-in tool Grok 1.0.70 offers, taken away (<c>--tools ""</c> takes none: 24 tools, 8,900 tokens a call).
    /// A name a later Grok drops is only warned about; one it adds shows in <c>tool_definitions_tokens</c> of its debug log.
    /// </summary>
    internal const string BuiltInTools =
        "read_file,write,search_replace,run_terminal_cmd,grep,list_dir,glob,web_search,web_fetch,x_search,task,get_task_output,"
        + "wait_tasks,kill_task,lsp,enter_plan_mode,exit_plan_mode,monitor,workflow,image_gen,image_edit,todo_write,search_tool,"
        + "skill,scheduler_list,hashline_read,hashline_grep,hashline_edit,edit_notebook,switch_mode,create_plan,use_tool,"
        + "update_goal,ask_user_question,send_feedback,image_to_video,reference_to_video,Agent";

    /// <summary>The skills Grok bundles (3,300 tokens a call): none is for Helm's jobs.</summary>
    private static readonly string[] BundledSkills =
    [
        "build-with-ai", "code-review", "create-skill", "create-workflow", "design", "docx", "execute-plan", "game-assets", "imagine",
        "implement", "learn", "long-running-background-tasks", "pdf", "pptx", "pr-babysit", "resume-claude", "resume-codex",
        "resume-cursor", "review", "skill-design-principles", "statusline", "threejs-frame-conventions",
    ];

    /// <summary>
    /// GROK_HOME, and a home folder of its own: Grok also reads the user's ~/.claude.json, ~/.claude (MCP servers, hooks,
    /// plugins) and ~/.cursor, which would hand it the user's own MCP tools and run their hooks on every turn (and
    /// cost 5 s of start-up). With an empty home, its Claude and Cursor compatibility off and no workflows it has none
    /// of them: 10 s a turn became 3.5 s.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ChildEnvironment(string home)
    {
        var user = Path.Combine(home, "user");
        Directory.CreateDirectory(user);
        var env = new Dictionary<string, string>(ClaudeCodeAgentFactory.Environment(HomeVariable, home), StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = user,
            ["HOME"] = user,
            ["GROK_WORKFLOWS"] = "0",
        };
        foreach (var vendor in new[] { "CLAUDE", "CURSOR" })
            foreach (var part in new[] { "AGENTS", "HOOKS", "MCPS", "RULES", "SKILLS" })
                env[$"GROK_{vendor}_{part}_ENABLED"] = "false";
        return env;
    }

    /// <summary>
    /// Turns the bundled skills and the workflows off in the agent's config.toml (Grok keeps its own sections there), once.
    /// What is left of a call is Helm's own prompt and about 640 tokens of Grok's.
    /// </summary>
    internal static void Configure(string home)
    {
        var path = Path.Combine(home, "config.toml");
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            if (text.Contains("[skills]", StringComparison.Ordinal)) return;
            var skills = string.Join(", ", BundledSkills.Select(s => $"\"{s}\""));
            File.AppendAllText(path, $"\n# Helm's agent: none of Grok's own skills or workflows.\n[skills]\ndisabled = [{skills}]\n\n[workflows]\nenabled = false\n",
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

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
        Configure(home);
        return new GrokChat(this, launcher, logger, home, WorkFolder, model, effort, system, tools);
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

/// <summary>
/// One conversation: each turn is one headless <c>grok --prompt-file</c> run in an empty folder, with none of Grok's own
/// tools, skills, workflows, web search or subagents, and Helm's instructions instead of Grok's coding prompt; later turns resume
/// the session Helm named on the first.
/// </summary>
internal sealed class GrokChat(GrokAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string workFolder,
    string model, string effort, string system, IReadOnlyList<AssistantTool> tools) : IAssistantChat
{
    private readonly string _session = Guid.NewGuid().ToString();
    private string _pending = "";
    private string _more = "";
    private bool _first = true;
    private bool _started;
    private int _calls;

    public void AddUser(string text)
    {
        _pending = _first ? $"{CliAgentProtocol.Preamble(system, tools)}\n\n{text}" : _more + text;
        _first = false;
        _more = "";
    }

    public void AddTools(IReadOnlyList<AssistantTool> added) => _more = CliAgentProtocol.MoreTools(added) + "\n";

    public void AddResults(IReadOnlyList<ToolOutcome> results) => _pending = CliAgentProtocol.Results(results);

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        var grok = GrokAgentFactory.FindGrok() ?? throw new AssistantException("Grok CLI is not installed on this PC.");
        var prompt = Path.Combine(workFolder, _session + ".txt");
        await File.WriteAllTextAsync(prompt, _pending, new UTF8Encoding(false), ct);
        try
        {
            string[] args =
            [
                "--prompt-file", prompt, "--output-format", "json", "--cwd", workFolder,
                "--disallowed-tools", GrokAgentFactory.BuiltInTools, "--disable-web-search", "--no-subagents", "--no-plan", "--no-alt-screen",
                "--system-prompt-override", "You are Helm's assistant. Follow the instructions in the user's first message exactly.",
                .. model.StartsWith("grok-", StringComparison.Ordinal) ? new[] { "-m", model } : [],
                // Grok thinks long by default (20 s for a one-line answer): Helm's jobs are short.
                "--reasoning-effort", effort is { Length: > 0 } ? effort : "low",
                .. _started ? new[] { "-r", _session } : ["-s", _session],
            ];
            var commandLine = grok.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? $"{CommandLine.Quote(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([grok, .. args])}\""
                : CommandLine.Join([grok, .. args]);
            ChildProcess process;
            try
            {
                process = launcher.Start(new ChildProcessStartInfo(commandLine, workFolder)
                {
                    Environment = GrokAgentFactory.ChildEnvironment(home),
                });
            }
            catch (ChildProcessException ex)
            {
                throw new AssistantException("Grok could not be started: " + ex.Message, ex);
            }
            using (process)
            using (ct.Register(() => { try { process.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }))
            {
                process.StandardInput.Close();
                var errors = ReadAllAsync(process.StandardError);
                var output = await ReadAllAsync(process.StandardOutput);
                await process.Exited;
                ct.ThrowIfCancellationRequested();
                var (answer, error, usage) = Read(output);
                if (answer is null)
                {
                    error ??= (await errors).Trim() is { Length: > 0 } e ? e : "it stopped without an answer";
                    if (error.Contains("limit", StringComparison.OrdinalIgnoreCase) || error.Contains("quota", StringComparison.OrdinalIgnoreCase))
                        factory.ReachedLimit();
                    if (error.Contains("login", StringComparison.OrdinalIgnoreCase) || error.Contains("unauthor", StringComparison.OrdinalIgnoreCase))
                        factory.SignedOut();
                    logger.LogWarning("Grok failed: {Error}", error);
                    throw new AssistantException($"Grok: {error}");
                }
                _started = true;
                // Without usage in the answer: estimated from the text (about 4 characters a token), for what it saved.
                return CliAgentProtocol.Parse(answer, usage ?? new AssistantUsage(_pending.Length / 4, answer.Length / 4, 0, 0), ref _calls);
            }
        }
        finally
        {
            try
            {
                File.Delete(prompt);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The answer in Grok's one JSON object (<c>{"text", "stopReason", "sessionId", "usage": {input_tokens,
    /// cache_read_input_tokens, output_tokens, reasoning_tokens}…}</c>), or its plain text, or the error it reports.
    /// </summary>
    internal static (string? Answer, string? Error, AssistantUsage? Usage) Read(string output)
    {
        var text = output.Trim();
        var start = text.IndexOf('{');
        if (start < 0) return (text.Length > 0 ? text : null, null, null);
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text[start..]);
        }
        catch (JsonException)
        {
            return (text, null, null);
        }
        if (json?["is_error"] is JsonValue failed && failed.TryGetValue<bool>(out var isError) && isError
            || json?["error"] is JsonNode)
            return (null, Text(json!["error"]) ?? Text(json["text"]) ?? "it stopped with an error", null);
        AssistantUsage? usage = null;
        if (json?["usage"] is JsonObject u)
        {
            long Count(string name) => u[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            var cached = Count("cache_read_input_tokens");
            usage = new AssistantUsage(Math.Max(0, Count("input_tokens") - cached), Count("output_tokens"), cached, Count("cache_creation_input_tokens"));
        }
        foreach (var name in new[] { "text", "result", "response", "content", "message", "output" })
            if (Text(json?[name]) is { } answer) return (answer, null, usage);
        return (null, "Helm could not read Grok's answer.", null);

        static string? Text(JsonNode? node) => node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonObject o => Text(o["message"]) ?? Text(o["text"]) ?? Text(o["content"]),
            JsonArray a => string.Concat(a.Select(Text)) is { Length: > 0 } joined ? joined : null,
            _ => null,
        };
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            return await reader.ReadToEndAsync();
        }
        catch (IOException)
        {
            return "";
        }
    }
}
