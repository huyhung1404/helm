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

    public static IReadOnlyList<(string Id, string Name)> All { get; } = [(Codex, "Codex"), (ClaudeCode, "Claude Code")];

    public static string NameOf(string kind) => All.FirstOrDefault(k => k.Id == kind).Name ?? kind;
}

/// <summary>
/// The proxy's agents on this PC: built from <see cref="AiAgentSettings"/>, tried in the user's order, each signed in
/// to the account the user chose for it (its own CODEX_HOME / CLAUDE_CONFIG_DIR under Helm's data, never the user's own
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
    private readonly object _gate = new();
    private List<(AgentProfile Profile, IAssistantChatFactory Factory, Func<string?> Unavailable, LocalAgentRoute Route)> _agents = [];
    private AgentChain _chain = new([]);

    public AgentRegistry(ISettingsStoreFactory settings, HelmPaths paths, IChildProcessLauncher launcher, IEnumerable<IMcpToolProvider> tools,
        Func<string?, bool> isToolOn, ILoggerFactory loggers)
    {
        _store = settings.Get<AiAgentSettings>(AiAgentSettings.StoreId);
        _paths = paths;
        _launcher = launcher;
        _tools = [.. tools];
        _isToolOn = isToolOn;
        _loggers = loggers;
        Build();
        _store.Changed += (_, _) => Build();
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

    /// <summary>"Ready", "Off", or why it takes no job.</summary>
    public string Status(AgentProfile profile)
    {
        if (!profile.Enabled) return "Off";
        lock (_gate)
        {
            var agent = _agents.FirstOrDefault(a => a.Profile.Id == profile.Id);
            return agent.Unavailable?.Invoke() is { } why ? why : agent.Factory is null ? "Off" : "Ready";
        }
    }

    public bool IsReady(AgentProfile profile) => Status(profile) == "Ready";

    public void Add(string kind)
    {
        var name = AgentKinds.NameOf(kind);
        var count = Profiles.Count(p => p.Kind == kind);
        _store.Update(s => s.Agents.Add(new AgentProfile { Kind = kind, Name = count == 0 ? name : $"{name} {count + 1}" }));
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
    /// /login). It starts through Explorer, so it runs with the user's normal rights even when Helm is elevated.
    /// </summary>
    public void SignIn(string id)
    {
        if (Profiles.FirstOrDefault(p => p.Id == id) is not { } profile) return;
        var home = Home(id);
        Directory.CreateDirectory(home);
        var (variable, steps) = profile.Kind == AgentKinds.ClaudeCode
            ? (ClaudeCodeAgentFactory.HomeVariable, "echo Sign in for Helm: type /login, sign in with the account Helm should use, then /exit.\r\nclaude")
            : (CodexAgentFactory.HomeVariable, "echo Sign in for Helm with the ChatGPT account Helm should use.\r\ncodex login");
        var script = Path.Combine(home, "sign-in.cmd");
        File.WriteAllText(script, $"@echo off\r\ntitle Helm: sign in {profile.Name}\r\nset {variable}={home}\r\ncd /d \"{home}\"\r\n{steps}\r\necho.\r\npause\r\n",
            new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{script}\"") { UseShellExecute = false });
    }

    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_gate)
            foreach (var agent in _agents) agent.Route.Dispose();
    }

    internal string Home(string id) => Path.Combine(_paths.ModuleDataDirectory("ai"), "agents", id);

    private void Build()
    {
        List<(AgentProfile, IAssistantChatFactory, Func<string?>, LocalAgentRoute)> agents = [];
        foreach (var profile in _store.Current.Agents.Where(p => p.Enabled))
        {
            var home = Home(profile.Id);
            var logger = _loggers.CreateLogger("Helm.App.Ai." + profile.Kind);
            (IAssistantChatFactory Factory, Func<string?> Unavailable)? made = profile.Kind switch
            {
                AgentKinds.Codex => new CodexAgentFactory(_launcher, home, logger) is var codex ? (codex, codex.Unavailable) : null,
                AgentKinds.ClaudeCode => new ClaudeCodeAgentFactory(_launcher, home, logger) is var claude ? (claude, claude.Unavailable) : null,
                _ => null,
            };
            if (made is not { } m) continue;
            agents.Add((profile, m.Factory, m.Unavailable,
                new LocalAgentRoute(Environment.MachineName, profile.Name, m.Factory, _tools, _isToolOn, m.Unavailable)));
        }
        List<(AgentProfile Profile, IAssistantChatFactory Factory, Func<string?> Unavailable, LocalAgentRoute Route)> old;
        lock (_gate)
        {
            old = _agents;
            _agents = agents;
            _chain = new AgentChain([.. agents.Select(a => a.Item4)]);
        }
        foreach (var agent in old) agent.Route.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
