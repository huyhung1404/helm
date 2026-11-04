using System.Diagnostics;
using Helm.Ai.Agents;
using Helm.Core.Services;
using Helm.Shell.Services;

namespace Helm.Cli.Hosting;

/// <summary>The box's host name, as the phone lists this worker ("fairy").</summary>
internal sealed class MachineDeviceInfo : IDeviceInfo
{
    public string DeviceName => Environment.MachineName;
}

/// <summary>No UI thread: what a view model posts runs at once, on the caller's thread.</summary>
internal sealed class HeadlessUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

/// <summary>
/// An agent's sign-in in the terminal helm runs in: its CLI's login with the agent's own folder as its home, then
/// Helm's marker when the CLI keeps no sign-in file of its own (Grok).
/// </summary>
internal sealed class TerminalAgentSignIn(TextWriter output) : IAgentSignIn
{
    public void Open(AgentSignIn signIn)
    {
        output.WriteLine($"Signing in {signIn.Name} ({signIn.HomeVariable}={signIn.Home})");
        output.WriteLine(signIn.Hint);
        var info = new ProcessStartInfo(signIn.Command[0]) { WorkingDirectory = signIn.Home, UseShellExecute = false };
        foreach (var arg in signIn.Command.Skip(1)) info.ArgumentList.Add(arg);
        info.Environment[signIn.HomeVariable] = signIn.Home;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{signIn.Command[0]} did not start.");
        process.WaitForExit();
        if (process.ExitCode == 0 && signIn.Marker is { } marker) File.WriteAllText(marker, "ok");
    }
}
