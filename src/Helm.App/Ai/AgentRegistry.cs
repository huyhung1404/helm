using System.Diagnostics;
using System.IO;
using System.Text;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.App.Ai;

/// <summary>One agent the user added to this PC's proxy, with its own sign-in folder.</summary>
public sealed class AgentProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary><see cref="AgentKinds"/>.</summary>
    public string Kind { get; set; } = AgentKinds.Codex;

    public string Name { get; set; } = "Codex";

    public bool Enabled { get; set; } = true;
}

/// <summary>This PC's agents, in the order the proxy tries them (settings/ai-agents.json: paths are this PC's own).</summary>
public sealed class AiAgentSettings : IVersionedSettings
{
    public const string StoreId = "ai-agents";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public List<AgentProfile> Agents { get; set; } = [];
}

/// <summary>The kinds of agent CLI Helm can run. A new one is a kind here and a chat factory.</summary>
public static class AgentKinds
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
    public const string Grok = "grok";

    public static IReadOnlyList<(string Id, string Name)> All { get; } = [(Codex, "Codex"), (ClaudeCode, "Claude Code"), (Grok, "Grok")];

    public static string NameOf(string kind) => All.FirstOrDefault(k => k.Id == kind).Name ?? kind;
}

/// <summary>
/// The proxy's agents on this PC: built from <see cref="AiAgentSettings"/>, tried in the user's order, each signed in
/// to the account the user chose for it (its own CODEX_HOME / CLAUDE_CONFIG_DIR / GROK_HOME under Helm's data, never the user's own
/// Claude Code or Codex login). It is this PC's agent route, for its own features and for the phone's jobs.
/// </summary>
internal sealed class AgentRegistry : IAgentRoute, IDisposable
{
    private readonly ISettingsStore<AiAgentSettings> _store;
    private readonly HelmPaths _paths;
    private readonly IChildProcessLauncher _launcher;
    private readonly IReadOnlyList<IMcpToolProvider> _tools;
    private readonly Func<string?, bool> _isToolOn;
    private readonly ILoggerFactory _loggers;
    private readonly Helm.Ai.Prompts.IAiPrompts? _prompts;
    private readonly Helm.Ai.AiKeyStore? _keys;
    private readonly object _gate = new();
    private IReadOnlyList<Agent> _agents = [];
    private readonly AgentChain _chain = new([]);
    private FileSystemWatcher? _signIns;
    private Timer? _settle;

    /// <param name="keys">The API keys whose last test lists the models an agent of their provider can be asked for.</param>
    public AgentRegistry(ISettingsStoreFactory settings, HelmPaths paths, IChildProcessLauncher launcher, IEnumerable<IMcpToolProvider> tools,
        Func<string?, bool> isToolOn, ILoggerFactory loggers, Helm.Ai.Prompts.IAiPrompts? prompts = null, Helm.Ai.AiKeyStore? keys = null)
    {
        _keys = keys;
        _store = settings.Get<AiAgentSettings>(AiAgentSettings.StoreId);
        _paths = paths;
        _launcher = launcher;
        _tools = [.. tools];
        _isToolOn = isToolOn;
        _loggers = loggers;
        _prompts = prompts;
        Build();
        _store.Changed += (_, _) => Build();
        WatchSignIns();
    }

    /// <summary>Raised after the list or a sign-in changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AgentProfile> Profiles => _store.Current.Agents;

    public string Device => Environment.MachineName;

    /// <summary>The agents that can take a job now, best first, as the phone sees them.</summary>
    public IReadOnlyList<string> Ready() => _chain.Ready();

    public string? Unavailable() => _chain.Unavailable();

    public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct) => _chain.RunAsync(job, wait, ct);

    public Task UndoAsync(AiJobAction action, CancellationToken ct) => _chain.UndoAsync(action, ct);

    public void End(string conversationId) => _chain.End(conversationId);

    public void Warm(AiJob job) => _chain.Warm(job);

    public IReadOnlyList<AiAgentOffer> Offers => _chain.Offers;

    public string? UnavailableFor(string agent) => _chain.UnavailableFor(agent);

    public bool ReadsPhotos => _chain.ReadsPhotos;

    /// <summary>"Ready", "Off", or why it takes no job.</summary>
    public string Status(AgentProfile profile)
    {
        if (!profile.Enabled) return "Off";
        lock (_gate)
        {
            var agent = _agents.FirstOrDefault(a => a.Profile.Id == profile.Id);
            return agent?.Unavailable() is { } why ? why : agent is null ? "Off" : "Ready";
        }
    }

    public bool IsReady(AgentProfile profile) => Status(profile) == "Ready";

    /// <summary>
    /// Adds an agent of <paramref name="kind"/> and opens its sign-in at once. While one of that kind still waits for its
    /// sign-in, that one signs in instead: pressing Add again never leaves a second, empty agent.
    /// </summary>
    public void Add(string kind)
    {
        var waiting = Profiles.FirstOrDefault(p => p.Kind == kind && !SignedIn(p));
        if (waiting is not null)
        {
            SignIn(waiting.Id);
            return;
        }
        var name = AgentKinds.NameOf(kind);
        var count = Profiles.Count(p => p.Kind == kind);
        var profile = new AgentProfile { Kind = kind, Name = count == 0 ? name : $"{name} {count + 1}" };
        _store.Update(s => s.Agents.Add(profile));
        SignIn(profile.Id);
    }

    public void Remove(string id)
    {
        var home = Home(id);
        _store.Update(s => s.Agents.RemoveAll(a => a.Id == id));
        // Its sign-in goes with it.
        try
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loggers.CreateLogger<AgentRegistry>().LogWarning(ex, "Could not remove the agent's folder {Home}", home);
        }
    }

    public void Move(string id, int by) => _store.Update(s =>
    {
        var i = s.Agents.FindIndex(a => a.Id == id);
        var to = i + by;
        if (i < 0 || to < 0 || to >= s.Agents.Count) return;
        (s.Agents[i], s.Agents[to]) = (s.Agents[to], s.Agents[i]);
    });

    public void SetEnabled(string id, bool enabled) => _store.Update(s =>
    {
        if (s.Agents.FirstOrDefault(a => a.Id == id) is { } agent) agent.Enabled = enabled;
    });

    public void Rename(string id, string name) => _store.Update(s =>
    {
        if (s.Agents.FirstOrDefault(a => a.Id == id) is { } agent && name.Trim().Length > 0) agent.Name = name.Trim();
    });

    /// <summary>
    /// Opens a terminal signed in to nothing but this agent's own folder, running its sign-in (codex login; claude, then
    /// /login; grok login, which then leaves Helm's marker). It starts through Explorer, so it runs with the user's
    /// normal rights even when Helm is elevated. npm's codex.cmd, claude.cmd and grok.cmd need <c>call</c>, or the
    /// script would end with them.
    /// </summary>
    public void SignIn(string id)
    {
        if (Profiles.FirstOrDefault(p => p.Id == id) is not { } profile) return;
        var home = Home(id);
        Directory.CreateDirectory(home);
        var (variable, steps) = profile.Kind switch
        {
            AgentKinds.ClaudeCode => (ClaudeCodeAgentFactory.HomeVariable,
                "echo Sign in for Helm: type /login, sign in with the account Helm should use, then /exit.\r\ncall claude"),
            AgentKinds.Grok => (GrokAgentFactory.HomeVariable,
                $"echo Sign in for Helm with the X / SuperGrok account Helm should use.\r\ncall \"{GrokAgentFactory.FindGrok() ?? "grok"}\" login && echo ok> \"{Path.Combine(home, GrokAgentFactory.SignedInMarker)}\""),
            _ => (CodexAgentFactory.HomeVariable, "echo Sign in for Helm with the ChatGPT account Helm should use.\r\ncall codex login"),
        };
        var script = Path.Combine(home, "sign-in.cmd");
        File.WriteAllText(script, $"@echo off\r\ntitle Helm: sign in {profile.Name}\r\nset {variable}={home}\r\ncd /d \"{home}\"\r\n{steps}\r\necho.\r\npause\r\n",
            new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{script}\"") { UseShellExecute = false });
    }

    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _signIns?.Dispose();
        _settle?.Dispose();
        lock (_gate)
            foreach (var agent in _agents) agent.Route.Dispose();
    }

    internal string Home(string id) => Path.Combine(_paths.ModuleDataDirectory("ai"), "agents", id);

    public bool SignedIn(AgentProfile profile) => profile.Kind switch
    {
        AgentKinds.ClaudeCode => ClaudeCodeAgentFactory.SignedIn(Home(profile.Id)),
        AgentKinds.Grok => GrokAgentFactory.SignedIn(Home(profile.Id)),
        _ => CodexAgentFactory.SignedIn(Home(profile.Id)),
    };

    /// <summary>
    /// A sign-in finishes in its own terminal: the credentials file it writes (auth.json, .credentials.json, Grok's
    /// marker) turns the
    /// agent ready here, and the phone hears of it, without pressing anything.
    /// </summary>
    private void WatchSignIns()
    {
        var root = Path.Combine(_paths.ModuleDataDirectory("ai"), "agents");
        try
        {
            Directory.CreateDirectory(root);
            var settle = _settle = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
            _signIns = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler seen = (_, e) =>
            {
                var file = Path.GetFileName(e.FullPath);
                if (file is "auth.json" or ".credentials.json" or GrokAgentFactory.SignedInMarker) settle.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
            };
            _signIns.Created += seen;
            _signIns.Changed += seen;
            _signIns.Deleted += seen;
            _signIns.Renamed += (sender, e) => seen(sender, e);
            _signIns.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _loggers.CreateLogger<AgentRegistry>().LogWarning(ex, "Cannot watch the agents' sign-ins in {Root}", root);
        }
    }

    /// <summary>
    /// Follows the user's list (<see cref="AgentRoster"/>): an agent whose profile (id and kind) is still on keeps its
    /// route, its conversations and its limits; a rename only renames it, a new order only reorders.
    /// </summary>
    private void Build()
    {
        IReadOnlyList<Agent> gone;
        lock (_gate)
        {
            var (now, left) = AgentRoster.Follow(_agents, _store.Current.Agents.Where(p => p.Enabled),
                (a, p) => a.Profile.Id == p.Id && a.Profile.Kind == p.Kind,
                (a, p) =>
                {
                    a.Route.AgentName = p.Name;
                    return a with { Profile = p };
                },
                Make);
            _agents = now;
            _chain.Use([.. now.Select(a => a.Route)]);
            gone = left;
        }
        foreach (var agent in gone) agent.Route.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Agent? Make(AgentProfile profile)
    {
        var home = Home(profile.Id);
        var logger = _loggers.CreateLogger("Helm.App.Ai." + profile.Kind);
        (IAssistantChatFactory Factory, Func<string?> Unavailable)? made = profile.Kind switch
        {
            AgentKinds.Codex => new CodexAgentFactory(_launcher, home, logger) is var codex ? (codex, codex.Unavailable) : null,
            AgentKinds.ClaudeCode => new ClaudeCodeAgentFactory(_launcher, home, logger) is var claude ? (claude, claude.Unavailable) : null,
            AgentKinds.Grok => new GrokAgentFactory(_launcher, home, logger) is var grok ? (grok, grok.Unavailable) : null,
            _ => null,
        };
        if (made is not { } m) return null;
        return new Agent(profile, m.Factory, m.Unavailable,
            new LocalAgentRoute(Environment.MachineName, profile.Name, m.Factory, _tools, _isToolOn, m.Unavailable, prompts: _prompts,
                logger: _loggers.CreateLogger<LocalAgentRoute>(), kind: profile.Kind, models: () => ModelsOf(profile.Kind)));
    }

    /// <summary>
    /// What an agent can be asked to run, the one it runs for a job of another kind first: Claude Code any Claude model
    /// (those the Claude key's last test listed, else the current three), Grok any Grok model (likewise). Codex runs its
    /// own only.
    /// </summary>
    internal IReadOnlyList<string> ModelsOf(string kind)
    {
        var (provider, first, known) = kind switch
        {
            AgentKinds.ClaudeCode => (Helm.Ai.AiProviders.Claude, Helm.Ai.ClaudeModels.Haiku, new[] { "claude-opus-5-5", "claude-sonnet-5-5", Helm.Ai.ClaudeModels.Haiku }),
            AgentKinds.Grok => (Helm.Ai.AiProviders.Grok, GrokAgentFactory.Model(""), new[] { Helm.Ai.GrokModels.Default }),
            _ => (null, "", []),
        };
        if (provider is null) return [];
        var listed = _keys?.DefaultFor(provider) is { } key ? Helm.Ai.AiModels.Chat(key.Value.LastCheck).Select(m => m.Id) : [];
        return [.. new[] { first }.Concat(listed).Concat(known).Distinct(StringComparer.Ordinal)];
    }

    private sealed record Agent(AgentProfile Profile, IAssistantChatFactory Factory, Func<string?> Unavailable, LocalAgentRoute Route);
}
