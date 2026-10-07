using System.Text;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Ssh;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>The AI agent tab: what agents ran on a session, drawn like a terminal beside the user's own shell.</summary>
public sealed class SshAgentConsoleTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public SshAgentConsoleTests() => _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private static string Text(AgentConsole console) => Encoding.UTF8.GetString(console.Attach(_ => { }));

    [Fact]
    public void A_run_shows_its_command_output_and_exit_code_with_terminal_line_ends()
    {
        var console = new AgentConsole();
        using var stop = new CancellationTokenSource();
        Assert.False(console.HasRuns);

        using (var run = console.Begin(null, "uptime", stop))
        {
            Assert.Equal(1, console.Running);
            run.Output(" 14:05 up 32 days\n");
            run.End(new MenuRunResult(0, ""), timedOut: false, timeoutSeconds: 120);
        }

        var text = Text(console);
        Assert.Contains("AI agent", text);
        Assert.Contains("$ uptime", text);
        Assert.Contains(" 14:05 up 32 days\r\n", text);
        Assert.Contains("✓ exit 0", text);
        Assert.True(console.HasRuns);
        Assert.Equal(0, console.Running);
    }

    [Fact]
    public void A_failed_run_shows_its_error_output_and_exit_code()
    {
        var console = new AgentConsole();
        using var stop = new CancellationTokenSource();
        var run = console.Begin("Restart app", "~/.helm/menu 'run' 'restart'", stop);
        run.End(new MenuRunResult(3, "no such app\n"), timedOut: false, timeoutSeconds: 120);

        var text = Text(console);
        Assert.Contains("· Restart app", text);
        Assert.Contains("no such app\r\n", text);
        Assert.Contains("✗ exit 3", text);
    }

    [Fact]
    public void Control_characters_in_a_command_cannot_redraw_the_screen()
    {
        var console = new AgentConsole();
        using var stop = new CancellationTokenSource();
        console.Begin(null, "echo \u001b[2J\u001b]0;x\u0007 done\nls", stop).Dispose();

        var text = Text(console);
        Assert.Contains("$ echo ?[2J?]0;x? done", text);
        Assert.Contains("> ls", text);
        Assert.Contains("Broken off", text);
    }

    [Fact]
    public void Stopping_cancels_the_runs_going_on_and_says_the_user_stopped_them()
    {
        var console = new AgentConsole();
        using var stop = new CancellationTokenSource();
        var run = console.Begin(null, "sleep 999", stop);

        console.StopAll();
        Assert.True(stop.IsCancellationRequested);
        Assert.True(run.StoppedByUser);
        run.End(new MenuRunResult(null, "Stopped."), timedOut: false, timeoutSeconds: 120);

        var text = Text(console);
        Assert.Contains("Stopped in Helm", text);
        Assert.DoesNotContain("Stopped.", text);
    }

    [Fact]
    public void An_attached_terminal_gets_what_was_shown_then_what_comes_next_and_a_clear()
    {
        var console = new AgentConsole();
        using var stop = new CancellationTokenSource();
        var run = console.Begin(null, "tail log", stop);
        var received = new List<string>();
        var snapshot = Encoding.UTF8.GetString(console.Attach(b => received.Add(Encoding.UTF8.GetString(b))));
        Assert.Contains("$ tail log", snapshot);

        run.Output("line\n");
        Assert.Equal(["line\r\n"], received);

        console.Clear();
        Assert.Equal("", Text(console));
        Assert.Contains("\u001b[3J", received[^1]);
    }

    // ---- The page's tabs ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_agent_tab_appears_once_an_agent_ran_something_and_is_marked_until_opened()
    {
        using var vm = NewViewModel();
        var session = new SshSession(new SshHost { Id = "h1", Address = "example.com", User = "me" }, 80, 24);
        vm.ActiveSession = session;
        Assert.False(vm.HasAgentTab);
        Assert.Same(session, vm.ActiveTerminal);

        using var stop = new CancellationTokenSource();
        var run = session.Agent.Begin(null, "uptime", stop);
        Assert.True(vm.HasAgentTab);
        Assert.True(vm.AgentRunning);
        Assert.Equal("AI agent · running", vm.AgentTabTitle);
        Assert.True(vm.AgentUnseen);
        // Nothing switches by itself: the user keeps typing in their shell.
        Assert.False(vm.ShowingAgent);
        Assert.True(vm.CanType);

        vm.ShowingAgent = true;
        Assert.Same(session.Agent, vm.ActiveTerminal);
        Assert.False(vm.ShowingShell);
        Assert.False(vm.AgentUnseen);
        Assert.False(vm.CanType);

        run.End(new MenuRunResult(0, ""), false, 120);
        Assert.False(vm.AgentRunning);
        Assert.False(vm.AgentUnseen);

        vm.ShowingShell = true;
        Assert.Same(session, vm.ActiveTerminal);
        Assert.True(vm.CanType);
    }

    [Fact]
    public void Another_server_opens_on_its_shell()
    {
        using var vm = NewViewModel();
        var first = new SshSession(new SshHost { Id = "h1", Address = "a.example.com", User = "me" }, 80, 24);
        var second = new SshSession(new SshHost { Id = "h2", Address = "b.example.com", User = "me" }, 80, 24);
        using var stop = new CancellationTokenSource();
        first.Agent.Begin(null, "uptime", stop);
        vm.ActiveSession = first;
        vm.ShowingAgent = true;

        vm.ActiveSession = second;
        Assert.False(vm.ShowingAgent);
        Assert.False(vm.HasAgentTab);
        Assert.Same(second, vm.ActiveTerminal);

        // The first server's runs no longer touch the page.
        first.Agent.Begin(null, "df -h", stop);
        Assert.False(vm.AgentUnseen);
    }

    private SshViewModel NewViewModel()
    {
        var key = new SshDeviceKey(Path.Combine(_dir.Path, "k.bin"), new PlainSecretProtector(), "pc");
        return new SshViewModel(_settings, key, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), NullLogger<SshViewModel>.Instance);
    }
}
