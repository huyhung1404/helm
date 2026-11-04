using Helm.Ai;
using Helm.Ai.Agents;
using Helm.Core.Processes;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The proxy's agents, now in Helm.Ai.Core (shared by Helm on Windows and helm on Linux): the list, the sign-ins and
/// the models behave as they did in Helm.App.
/// </summary>
public sealed class AgentRegistryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HelmPaths _paths;
    private readonly SettingsStoreFactory _settings;
    private readonly RecordingSignIn _signIn = new();
    private readonly AgentRegistry _registry;

    public AgentRegistryTests()
    {
        _paths = new HelmPaths(_dir.Path);
        _settings = new SettingsStoreFactory(_paths);
        _registry = new AgentRegistry(_settings, _paths, new NoLauncher(), _signIn, [], _ => true, NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _settings.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void AddingAnAgentOpensItsSignInInItsOwnFolder()
    {
        _registry.Add(AgentKinds.Codex);

        var profile = Assert.Single(_registry.Profiles);
        Assert.Equal("Codex", profile.Name);
        var signIn = Assert.Single(_signIn.Opened);
        Assert.Equal(AgentRegistry.HomeOf(_paths, profile.Id), signIn.Home);
        Assert.True(Directory.Exists(signIn.Home));
        Assert.Equal("CODEX_HOME", signIn.HomeVariable);
        Assert.Equal(["codex", "login"], signIn.Command);
        Assert.Null(signIn.Marker);
    }

    [Fact]
    public void AddingAgainWhileOneWaitsForItsSignInSignsThatOneIn()
    {
        _registry.Add(AgentKinds.ClaudeCode);
        _registry.Add(AgentKinds.ClaudeCode);

        Assert.Single(_registry.Profiles);
        Assert.Equal(2, _signIn.Opened.Count);
        Assert.All(_signIn.Opened, s => Assert.Equal("CLAUDE_CONFIG_DIR", s.HomeVariable));

        // Signed in: the next Add is a second agent, numbered.
        File.WriteAllText(Path.Combine(AgentRegistry.HomeOf(_paths, _registry.Profiles[0].Id), ".credentials.json"), "{}");
        _registry.Add(AgentKinds.ClaudeCode);
        Assert.Equal(["Claude Code", "Claude Code 2"], _registry.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void GrokSignsInWithHelmsMarker()
    {
        _registry.Add(AgentKinds.Grok);

        var signIn = Assert.Single(_signIn.Opened);
        Assert.Equal("GROK_HOME", signIn.HomeVariable);
        Assert.Equal("login", signIn.Command[^1]);
        Assert.Equal(Path.Combine(signIn.Home, GrokAgentFactory.SignedInMarker), signIn.Marker);
    }

    [Fact]
    public void EachKindIsSignedInByItsOwnFile()
    {
        foreach (var (kind, file) in new[] { (AgentKinds.Codex, "auth.json"), (AgentKinds.ClaudeCode, ".credentials.json"), (AgentKinds.Grok, ".helm-signed-in") })
        {
            var profile = new AgentProfile { Kind = kind };
            Assert.False(AgentRegistry.IsSignedIn(_paths, profile));
            var home = AgentRegistry.HomeOf(_paths, profile.Id);
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, file), "x");
            Assert.True(AgentRegistry.IsSignedIn(_paths, profile));
        }
    }

    [Fact]
    public void ListChangesKeepOrderNamesAndSwitches()
    {
        _registry.Add(AgentKinds.Codex);
        _registry.Add(AgentKinds.Grok);
        var (codex, grok) = (_registry.Profiles[0].Id, _registry.Profiles[1].Id);

        _registry.Move(grok, -1);
        _registry.Rename(codex, "  Work Codex ");
        _registry.Rename(codex, "   ");
        _registry.SetEnabled(grok, false);

        Assert.Equal([grok, codex], _registry.Profiles.Select(p => p.Id));
        Assert.Equal("Work Codex", _registry.Profiles[1].Name);
        Assert.Equal("Off", _registry.Status(_registry.Profiles[0]));
        // Not signed in (or not installed on the machine running the tests): never ready, no job.
        Assert.NotEqual("Ready", _registry.Status(_registry.Profiles[1]));
        Assert.Empty(_registry.Ready());
    }

    [Fact]
    public void RemovingAnAgentRemovesItsSignIn()
    {
        _registry.Add(AgentKinds.Codex);
        var id = _registry.Profiles[0].Id;
        var home = AgentRegistry.HomeOf(_paths, id);
        File.WriteAllText(Path.Combine(home, "auth.json"), "{}");

        _registry.Remove(id);

        Assert.Empty(_registry.Profiles);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public void ModelsAreTheKindsOwnWithoutKeys()
    {
        Assert.Equal([ClaudeModels.Haiku, "claude-opus-5-5", "claude-sonnet-5-5"], _registry.ModelsOf(AgentKinds.ClaudeCode));
        Assert.Equal(GrokAgentFactory.Model(""), _registry.ModelsOf(AgentKinds.Grok)[0]);
        Assert.Contains(GrokModels.Default, _registry.ModelsOf(AgentKinds.Grok));
        Assert.Empty(_registry.ModelsOf(AgentKinds.Codex));
    }

    [Fact]
    public async Task ASignInFinishedInItsTerminalTellsTheRegistry()
    {
        _registry.Add(AgentKinds.Codex);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.Changed += (_, _) => changed.TrySetResult();

        File.WriteAllText(Path.Combine(AgentRegistry.HomeOf(_paths, _registry.Profiles[0].Id), "auth.json"), "{}");

        Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.True(_registry.SignedIn(_registry.Profiles[0]));
    }

    private sealed class RecordingSignIn : IAgentSignIn
    {
        public List<AgentSignIn> Opened { get; } = [];

        public void Open(AgentSignIn signIn) => Opened.Add(signIn);
    }

    private sealed class NoLauncher : IChildProcessLauncher
    {
        public ChildProcess Start(ChildProcessStartInfo startInfo) => throw new ChildProcessException("No process in these tests.");
    }
}
