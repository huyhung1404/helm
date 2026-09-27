using System.Text;
using Helm.Core.Processes;
using Helm.Modules.ClaudeChat.Cli;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public class ClaudeCliTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    public void Command_line_quoting_follows_argv_rules(string arg, string expected) => Assert.Equal(expected, CommandLine.Quote(arg));

    [Fact]
    public void Native_cli_command_line_is_quoted()
    {
        var cli = new ClaudeCli(@"C:\Program Files\claude\claude.exe");

        Assert.Equal("\"C:\\Program Files\\claude\\claude.exe\" -p --model sonnet", cli.BuildCommandLine(["-p", "--model", "sonnet"]));
    }

    [Fact]
    public void Npm_shim_runs_through_cmd()
    {
        var cli = new ClaudeCli(@"C:\Users\me\AppData\Roaming\npm\claude.cmd");

        var line = cli.BuildCommandLine(["-p", "--resume", "00000000-0000-4000-8000-000000000001"]);

        Assert.True(cli.IsBatchScript);
        Assert.Contains("/d /s /c \"", line);
        Assert.EndsWith("claude.cmd -p --resume 00000000-0000-4000-8000-000000000001\"", line);
    }

    [Theory]
    [InlineData("a&calc")]
    [InlineData("x|y")]
    [InlineData("%PATH%")]
    [InlineData("a^b")]
    [InlineData("<in")]
    public void Npm_shim_rejects_arguments_cmd_would_interpret(string arg)
    {
        var cli = new ClaudeCli(@"C:\npm\claude.cmd");

        Assert.Throws<ArgumentException>(() => cli.BuildCommandLine(["--model", arg]));
    }

    [Fact]
    public void Session_arguments_always_route_permissions_to_helm()
    {
        var args = ClaudeSession.BuildArguments(new ClaudeSessionOptions(@"C:\work") { Model = "sonnet", ResumeSessionId = "abc" });

        Assert.Equal(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--permission-prompt-tool", "stdio", "--model", "sonnet", "--resume", "abc"], args);
    }

    [Theory]
    [InlineData(ClaudePermissionMode.AcceptEdits, "acceptEdits")]
    [InlineData(ClaudePermissionMode.Plan, "plan")]
    public void Non_default_permission_modes_are_passed(ClaudePermissionMode mode, string expected)
    {
        var args = ClaudeSession.BuildArguments(new ClaudeSessionOptions(@"C:\work") { PermissionMode = mode });

        Assert.Equal(expected, args[args.ToList().IndexOf("--permission-mode") + 1]);
    }

    [Fact]
    public void Session_arguments_never_bypass_permissions()
    {
        foreach (var mode in Enum.GetValues<ClaudePermissionMode>())
        {
            var args = ClaudeSession.BuildArguments(new ClaudeSessionOptions(@"C:\work") { PermissionMode = mode });
            Assert.DoesNotContain("bypassPermissions", args);
            Assert.DoesNotContain("--dangerously-skip-permissions", args);
        }
    }

    [Fact]
    public void Environment_drops_parent_session_variables_only()
    {
        var env = ClaudeSession.BuildEnvironment(new Dictionary<string, string>
        {
            ["PATH"] = @"C:\bin",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_MESSAGING_TOKEN"] = "secret",
            ["claude_code_entrypoint"] = "sdk",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
        });

        Assert.Equal(@"C:\bin", env["PATH"]);
        Assert.Equal("1", env["CLAUDE_CODE_USE_BEDROCK"]);
        Assert.False(env.ContainsKey("CLAUDECODE"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_MESSAGING_TOKEN"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
    }

    [Fact]
    public void Locator_follows_path_order()
    {
        using var dirs = new TempDirs(3);
        File.WriteAllText(Path.Combine(dirs[0], "claude.cmd"), "");
        File.WriteAllText(Path.Combine(dirs[1], "claude.exe"), "");

        var cli = ClaudeCliLocator.Find(pathVariable: $"{dirs[0]};{dirs[1]}", userProfile: dirs[2]);

        Assert.Equal(Path.Combine(dirs[0], "claude.cmd"), cli?.Path);
    }

    [Fact]
    public void Locator_prefers_exe_within_one_folder()
    {
        using var dirs = new TempDirs(2);
        File.WriteAllText(Path.Combine(dirs[0], "claude.cmd"), "");
        File.WriteAllText(Path.Combine(dirs[0], "claude.exe"), "");

        Assert.Equal(Path.Combine(dirs[0], "claude.exe"), ClaudeCliLocator.Find(pathVariable: dirs[0], userProfile: dirs[1])?.Path);
    }

    [Fact]
    public void Locator_falls_back_to_the_native_installer_location()
    {
        using var dirs = new TempDirs(2);
        var native = Path.Combine(dirs[1], ".local", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "");

        Assert.Equal(native, ClaudeCliLocator.Find(pathVariable: dirs[0], userProfile: dirs[1])?.Path);

        File.WriteAllText(Path.Combine(dirs[0], "claude.cmd"), "");
        Assert.Equal(Path.Combine(dirs[0], "claude.cmd"), ClaudeCliLocator.Find(pathVariable: dirs[0], userProfile: dirs[1])?.Path);
    }

    [Fact]
    public void Locator_honours_override_and_reports_missing()
    {
        using var dirs = new TempDirs(1);
        var custom = Path.Combine(dirs[0], "my-claude.exe");
        File.WriteAllText(custom, "");

        Assert.Equal(custom, ClaudeCliLocator.Find(custom)?.Path);
        Assert.Null(ClaudeCliLocator.Find(Path.Combine(dirs[0], "missing.exe")));
        Assert.Null(ClaudeCliLocator.Find(pathVariable: dirs[0] + ";\"bad<path\"", userProfile: dirs[0]));
    }

    [Fact]
    public async Task Child_process_pipes_stdin_and_stdout()
    {
        var launcher = new ChildProcessLauncher(NullLogger<ChildProcessLauncher>.Instance);
        using var child = launcher.Start(new ChildProcessStartInfo("cmd.exe /d /q /k", Path.GetTempPath()));

        Assert.True(child.KillsProcessTree);
        await child.StandardInput.WriteAsync(Encoding.ASCII.GetBytes("echo helm-%HELM_TEST%\r\nexit 7\r\n"));
        await child.StandardInput.FlushAsync();
        var output = await new StreamReader(child.StandardOutput).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("helm-", output);
        Assert.Equal(7, await child.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Child_process_uses_the_given_environment()
    {
        var launcher = new ChildProcessLauncher(NullLogger<ChildProcessLauncher>.Instance);
        var env = ClaudeSession.BuildEnvironment();
        env["HELM_TEST"] = "from-helm";
        using var child = launcher.Start(new ChildProcessStartInfo("cmd.exe /d /c echo %HELM_TEST%", Path.GetTempPath()) { Environment = env });

        var output = await new StreamReader(child.StandardOutput).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("from-helm", output.Trim());
    }

    [Fact]
    public async Task Disposing_kills_the_whole_process_tree()
    {
        var launcher = new ChildProcessLauncher(NullLogger<ChildProcessLauncher>.Instance);
        var before = PingPids();
        var child = launcher.Start(new ChildProcessStartInfo("cmd.exe /d /c ping -n 60 127.0.0.1 >nul", Path.GetTempPath()));
        var grandchildren = new List<int>();
        await WaitUntilAsync(() => (grandchildren = PingPids().Except(before).ToList()).Count > 0, TimeSpan.FromSeconds(5));

        child.Dispose();

        await child.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var pid in grandchildren)
        {
            await WaitUntilAsync(() => !IsRunning(pid), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Run the suite from an elevated terminal to cover the shell-token path: the child must come out at medium
    /// integrity (S-1-16-8192), never high (S-1-16-12288), even though the test process is administrator.
    /// Where Windows gives an administrator no filtered token (UAC off, or the built-in Administrator without
    /// admin approval mode) every process of theirs, the shell included, runs at high integrity: there is no lower
    /// token to use, so there is nothing to check.
    /// </summary>
    [Fact]
    public async Task Child_process_is_never_elevated()
    {
        if (!UacSplitsAdminTokens()) return;
        var launcher = new ChildProcessLauncher(NullLogger<ChildProcessLauncher>.Instance);
        using var child = launcher.Start(new ChildProcessStartInfo("whoami.exe /groups /fo csv", Path.GetTempPath()));

        var output = await new StreamReader(child.StandardOutput).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // On failure, say which integrity the child got and who ran the test.
        var levels = string.Join(", ", output.Split('\n').Where(l => l.Contains("S-1-16-")).Select(l => l.Trim()));
        var context = $"child integrity: [{levels}]; test user: {System.Security.Principal.WindowsIdentity.GetCurrent().User}; elevated: {new Helm.Core.Services.ProcessLauncher(NullLogger<Helm.Core.Services.ProcessLauncher>.Instance).IsElevated}";
        Assert.True(output.Contains("S-1-16-8192") && !output.Contains("S-1-16-12288"), context);
    }

    [Fact]
    public void Missing_working_directory_is_reported()
    {
        var launcher = new ChildProcessLauncher(NullLogger<ChildProcessLauncher>.Instance);

        Assert.Throws<ChildProcessException>(() => launcher.Start(new ChildProcessStartInfo("cmd.exe", @"C:\does\not\exist\helm")));
    }

    /// <summary>
    /// Whether this administrator gets a filtered, medium-integrity token for normal processes: UAC on
    /// (EnableLUA=1), and either not the built-in Administrator (RID 500) or admin approval mode on for it
    /// (FilterAdministratorToken=1).
    /// </summary>
    private static bool UacSplitsAdminTokens()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if (key?.GetValue("EnableLUA") is not int lua || lua == 0) return false;
        var builtInAdmin = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value.EndsWith("-500", StringComparison.Ordinal) == true;
        return !builtInAdmin || key.GetValue("FilterAdministratorToken") is int filter && filter != 0;
    }

    private static HashSet<int> PingPids() => System.Diagnostics.Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(100);
        }
    }

    private sealed class TempDirs : IDisposable
    {
        private readonly string[] _dirs;

        public TempDirs(int count) =>
            _dirs = Enumerable.Range(0, count).Select(_ => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "helm-test-" + Guid.NewGuid().ToString("N"))).FullName).ToArray();

        public string this[int i] => _dirs[i];

        public void Dispose()
        {
            foreach (var d in _dirs)
            {
                try { Directory.Delete(d, recursive: true); } catch (IOException) { }
            }
        }
    }
}
