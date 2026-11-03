using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Helm.Ai.Assistant;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.App.Ai;

/// <summary>
/// xAI's Grok CLI (Grok Build, on a SuperGrok or X Premium+ plan, signed in for Helm in its own folder: GROK_HOME) as the
/// brain of Helm's agent jobs on this PC. A conversation is one <c>grok agent stdio</c> process with none of Grok's own
/// tools (see <see cref="GrokChat"/>), else one headless run a turn; Helm lists its tools in the first message and runs
/// the calls Grok asks for. Photos go only in agent mode.
/// </summary>
internal sealed class GrokAgentFactory(IChildProcessLauncher launcher, string home, ILogger logger) : IAssistantChatFactory
{
    public const string HomeVariable = "GROK_HOME";

    /// <summary>Written by Helm's sign-in terminal once <c>grok login</c> succeeded (the CLI's own token file is not documented).</summary>
    internal const string SignedInMarker = ".helm-signed-in";

    private string WorkFolder => Path.Combine(home, "work");

    /// <summary>The system prompt in place of Grok's coding one: Helm's own instructions come in the first message.</summary>
    internal const string SystemPrompt = "You are Helm's assistant. Follow the instructions in the user's first message exactly.";

    private DateTimeOffset _limitedUntil;
    private int _acpRefused;

    public Helm.Ai.AiProvider Provider => Helm.Ai.AiProviders.Grok;

    /// <summary>
    /// Photos go as image blocks of an agent-mode prompt (<see cref="GrokAcp"/>); a headless run has no way in for one,
    /// so once agent mode was refused here Grok takes no photo job.
    /// </summary>
    public bool ReadsImages => !AcpRefused;

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

    /// <summary>
    /// The agent profile of an agent-mode session (<c>session/new</c> <c>_meta.agentProfile</c>, which a local
    /// <c>grok agent</c> takes as an object; <c>--disallowed-tools</c> is not read there): Grok's prompt replaced, no
    /// default tools, the built-ins taken away under both their names (it shows five under another one:
    /// run_terminal_command, spawn_subagent…), no MCP, skills or AGENTS.md. Grok 1.0.70 then lists 0 tools and a
    /// "Chào em" turn takes 1,477 input tokens; with no profile it has 26 tools and 13,600 tokens.
    /// </summary>
    internal static JsonObject SessionMeta() => new()
    {
        ["systemPromptOverride"] = SystemPrompt,
        ["agentProfile"] = new JsonObject
        {
            ["name"] = "helm-assistant",
            ["description"] = "Helm's assistant, with no tools of its own.",
            ["promptMode"] = "full",
            ["injectDefaultTools"] = false,
            ["tools"] = new JsonArray("helm_none"),
            ["disallowedTools"] = new JsonArray([.. BuiltInTools.Split(',').Concat(AcpToolNames).Select(t => (JsonNode)t)]),
            ["mcpInheritance"] = "none",
            ["agentsMd"] = false,
            ["discoverSkills"] = false,
            ["inheritSkills"] = false,
            ["skills"] = new JsonArray(),
        },
    };

    /// <summary>The names agent mode shows for built-ins whose id differs, and the scheduler tools it adds.</summary>
    private static readonly string[] AcpToolNames =
    [
        "run_terminal_command", "kill_command_or_subagent", "get_command_or_subagent_output", "wait_commands_or_subagents",
        "spawn_subagent", "scheduler_create", "scheduler_delete",
    ];

    /// <summary>
    /// <c>grok agent stdio</c> on its own (no shared leader: a leader's daemon route runs the default agent for a
    /// profile object), never approving a tool by itself.
    /// </summary>
    internal static string[] AgentArguments(string model, string effort) =>
        ["agent", "--no-leader", "-m", Model(model), "--reasoning-effort", Effort(effort), "stdio"];

    /// <summary>A Grok model picked in the card runs as picked; any other pick on the plan's fastest (1.2 s for a short turn).</summary>
    internal static string Model(string model) => model.StartsWith("grok-", StringComparison.Ordinal) ? model : "grok-4.7-build-fast";

    /// <summary>Grok thinks long by default (20 s for a one-line answer): Helm's jobs are short.</summary>
    internal static string Effort(string effort) => effort is { Length: > 0 } ? effort : "low";

    /// <summary>The command line for <paramref name="grok"/>: npm's grok.cmd only runs through cmd.exe.</summary>
    internal static string CommandLineFor(string grok, IEnumerable<string> args) =>
        grok.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"{CommandLine.Quote(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([grok, .. args])}\""
            : CommandLine.Join([grok, .. args]);

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
            // Work a headless turn does not need, each named in the docs Grok carries (grok.exe): the update check on
            // launch, telemetry and feedback uploads, remote campaign patches, and the session title, turn summary and
            // recap it writes with model calls of its own.
            ["GROK_DISABLE_AUTOUPDATER"] = "1",
            ["DISABLE_TELEMETRY"] = "1",
            ["GROK_FEEDBACK_ENABLED"] = "0",
            ["GROK_CAMPAIGNS"] = "0",
            ["GROK_TITLE_REFRESH"] = "0",
            ["GROK_TURN_SUMMARY"] = "0",
            ["GROK_SESSION_RECAP"] = "0",
            // Agent mode turns Grok's memory on (its notes in the prompt, 300 tokens a turn, and a capture after
            // each one); no subagents either way.
            ["GROK_MEMORY"] = "0",
            ["GROK_SUBAGENTS"] = "0",
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

    /// <summary>
    /// What an error of Grok's says about the agent: a plan's limit or a sign-in that no longer works. True for
    /// either (any other run would fail the same way).
    /// </summary>
    internal bool Failed(string error)
    {
        var limited = error.Contains("limit", StringComparison.OrdinalIgnoreCase) || error.Contains("quota", StringComparison.OrdinalIgnoreCase);
        var signedOut = error.Contains("login", StringComparison.OrdinalIgnoreCase) || error.Contains("unauthor", StringComparison.OrdinalIgnoreCase);
        if (limited) ReachedLimit();
        if (signedOut) SignedOut();
        return limited || signedOut;
    }

    /// <summary>Agent mode could not be used (tools Grok kept, a session it did not open): every turn runs headless from now on.</summary>
    internal bool AcpRefused => Volatile.Read(ref _acpRefused) != 0;

    internal void RefuseAcp(string why)
    {
        if (Interlocked.Exchange(ref _acpRefused, 1) == 0)
            logger.LogWarning("Grok's agent mode is not used ({Reason}); each turn runs Grok headless", why);
    }

    /// <summary>
    /// Deletes what Grok keeps of a session on disk (GROK_HOME/sessions/&lt;work folder&gt;/&lt;id&gt;: its chat history, with a
    /// photo's bytes), once the conversation that sent the photo ended. One still in use is left; never throws.
    /// </summary>
    internal static void ForgetSession(string home, string session)
    {
        var sessions = Path.Combine(home, "sessions");
        try
        {
            if (!Directory.Exists(sessions)) return;
            foreach (var folder in Directory.EnumerateDirectories(sessions, session, SearchOption.AllDirectories).ToList())
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
/// One conversation with Grok: one <c>grok agent stdio</c> process whose session stays loaded between turns
/// (<see cref="GrokAgentMode"/>), with none of Grok's own tools (<see cref="GrokAgentFactory.SessionMeta"/>; a session
/// that lists any is refused), no file or terminal access for it, and every permission it asks for cancelled. When
/// agent mode cannot be used, each turn is one headless run (<see cref="GrokHeadless"/>).
/// </summary>
internal sealed class GrokChat : IAssistantChat, IDisposable
{
    private readonly GrokAgentFactory _factory;
    private readonly IChildProcessLauncher _launcher;
    private readonly ILogger _logger;
    private readonly string _home, _workFolder, _model, _effort;
    private readonly CliConversation _conversation;
    private readonly GrokHeadless _headless;
    private readonly GrokAgentMode _agent;
    private int _calls;
    private bool _photoSent;

    public GrokChat(GrokAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string workFolder,
        string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
    {
        (_factory, _launcher, _logger, _home, _workFolder, _model, _effort) = (factory, launcher, logger, home, workFolder, model, effort);
        _conversation = new CliConversation(system, tools);
        _headless = new GrokHeadless(factory, launcher, logger, home, workFolder, model, effort);
        _agent = new GrokAgentMode(Start, workFolder, GrokAgentFactory.SessionMeta(), factory.Failed);
    }

    public void AddUser(string text) => _conversation.AddUser(text);

    public void AddUser(string text, AssistantImage image)
    {
        _conversation.AddUser(text, image);
        _photoSent = true;
    }

    /// <summary>Starts Grok and opens its session while the user speaks (<see cref="GrokAgentMode.WarmAsync"/>).</summary>
    public Task WarmAsync(CancellationToken ct) => _factory.AcpRefused ? Task.CompletedTask : _agent.WarmAsync(ct);

    public void AddTools(IReadOnlyList<AssistantTool> added) => _conversation.AddTools(added);

    public void AddResults(IReadOnlyList<ToolOutcome> results) => _conversation.AddResults(results);

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        var grok = GrokAgentFactory.FindGrok() ?? throw new AssistantException("Grok CLI is not installed on this PC.");
        (string Answer, AssistantUsage Usage)? turn = null;
        if (!_factory.AcpRefused)
        {
            try
            {
                turn = await _agent.TurnAsync(_conversation, ct);
            }
            catch (AssistantException ex)
            {
                _logger.LogWarning("Grok failed: {Error}", ex.Message);
                throw;
            }
            if (turn is null && _agent.Refused is { } why) _factory.RefuseAcp(why);
        }
        if (turn is null && _conversation.Images.Count > 0) throw new AssistantImageException("Grok headless cannot read photos.");
        var (answer, usage) = turn ?? await _headless.TurnAsync(grok, _conversation, ct);
        _conversation.Answered(answer);
        return CliAgentProtocol.Parse(answer, usage, ref _calls);
    }

    public void Dispose()
    {
        _agent.Dispose();
        // A session that saw a photo keeps its bytes on disk: it goes with the conversation.
        if (_photoSent)
            foreach (var session in _agent.Sessions) GrokAgentFactory.ForgetSession(_home, session);
    }

    /// <summary>Starts <c>grok agent stdio</c> in the empty work folder, with the agent's own GROK_HOME and nothing of the user's.</summary>
    private GrokAgentProcess Start()
    {
        var grok = GrokAgentFactory.FindGrok() ?? throw new AssistantException("Grok CLI is not installed on this PC.");
        ChildProcess process;
        try
        {
            process = _launcher.Start(new ChildProcessStartInfo(GrokAgentFactory.CommandLineFor(grok, GrokAgentFactory.AgentArguments(_model, _effort)), _workFolder)
            {
                Environment = GrokAgentFactory.ChildEnvironment(_home),
            });
        }
        catch (ChildProcessException ex)
        {
            throw new AssistantException("Grok could not be started: " + ex.Message, ex);
        }
        _ = Drain(process.StandardError);
        _logger.LogInformation("Grok started in agent mode for an agent job ({Model})", GrokAgentFactory.Model(_model));
        return new GrokAgentProcess(new StreamReader(process.StandardOutput, Encoding.UTF8), new StreamWriter(process.StandardInput, new UTF8Encoding(false)),
            () => process.HasExited, () =>
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                process.Dispose();
            });
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
