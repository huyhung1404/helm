using System.Text;
using Helm.Core.Processes;

namespace Helm.Tests;

/// <summary>
/// The command lines Helm builds for its child processes, read back into arguments for Linux, and the plain launcher
/// helm uses there.
/// </summary>
public sealed class ChildProcessTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("say \"hi\"")]
    [InlineData(@"C:\path\ends\")]
    [InlineData(@"C:\a b\")]
    [InlineData(@"back\\""slash")]
    [InlineData("{\"disableAllHooks\":true}")]
    [InlineData("tab\there")]
    public void SplitReadsBackWhatJoinWrote(string arg)
    {
        string[] args = ["/usr/bin/claude", arg, "-p", arg];

        Assert.Equal(args, CommandLine.Split(CommandLine.Join(args)));
    }

    [Fact]
    public void SplitFollowsTheWindowsRules()
    {
        Assert.Equal(["a", "b c", "d"], CommandLine.Split("  a \"b c\"\td  "));
        Assert.Equal(["a\\\\b", "c\"d", "e\\"], CommandLine.Split("a\\\\b c\\\"d \"e\\\\\""));
        Assert.Equal(["say \"x\""], CommandLine.Split("\"say \"\"x\"\"\""));
        Assert.Empty(CommandLine.Split("   "));
    }

    [Fact]
    public async Task ThePlainLauncherRunsWithItsOwnEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        env["HELM_TEST_HOME"] = "agent home";
        string[] command = OperatingSystem.IsWindows()
            ? [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d", "/c", "echo %HELM_TEST_HOME%"]
            : ["/bin/sh", "-c", "echo \"$HELM_TEST_HOME\""];

        using var process = new PlainChildProcessLauncher().Start(new ChildProcessStartInfo(CommandLine.Join(command), Path.GetTempPath()) { Environment = env });
        process.StandardInput.Close();
        using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8);
        var output = await reader.ReadToEndAsync();

        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("agent home", output.Trim());
    }

    [Fact]
    public async Task ThePlainLauncherKillsAndRefusesMissingFolders()
    {
        string[] command = OperatingSystem.IsWindows()
            ? [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d", "/c", "ping -n 30 127.0.0.1 > nul"]
            : ["/bin/sh", "-c", "sleep 30"];
        using var process = new PlainChildProcessLauncher().Start(new ChildProcessStartInfo(CommandLine.Join(command), Path.GetTempPath()));

        Assert.False(process.HasExited);
        process.Kill();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(process.HasExited);
        Assert.Throws<ChildProcessException>(() =>
            new PlainChildProcessLauncher().Start(new ChildProcessStartInfo("anything", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))));
        Assert.Throws<ChildProcessException>(() =>
            new PlainChildProcessLauncher().Start(new ChildProcessStartInfo("helm-no-such-command-" + Guid.NewGuid().ToString("N"), Path.GetTempPath())));
    }
}
