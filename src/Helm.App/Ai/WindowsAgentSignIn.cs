using System.Diagnostics;
using System.IO;
using System.Text;
using Helm.Ai.Agents;

namespace Helm.App.Ai;

/// <summary>
/// An agent's sign-in in a console window of its own: a sign-in.cmd in the agent's folder that sets its home variable
/// and runs the CLI's login. It starts through Explorer, so it runs with the user's normal rights even when Helm is
/// elevated. npm's codex.cmd, claude.cmd and grok.cmd need <c>call</c>, or the script would end with them.
/// </summary>
internal sealed class WindowsAgentSignIn : IAgentSignIn
{
    public void Open(AgentSignIn signIn)
    {
        var script = Path.Combine(signIn.Home, "sign-in.cmd");
        File.WriteAllText(script, Script(signIn), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{script}\"") { UseShellExecute = false });
    }

    internal static string Script(AgentSignIn signIn)
    {
        // A full path (Grok's native grok.exe) is quoted; a bare name runs whatever the terminal finds on PATH.
        var cli = signIn.Command[0].IndexOfAny([Path.DirectorySeparatorChar, ' ']) >= 0 ? $"\"{signIn.Command[0]}\"" : signIn.Command[0];
        var command = string.Join(' ', [cli, .. signIn.Command.Skip(1)]);
        var marker = signIn.Marker is { } file ? $" && echo ok> \"{file}\"" : "";
        return $"@echo off\r\ntitle Helm: sign in {signIn.Name}\r\nset {signIn.HomeVariable}={signIn.Home}\r\ncd /d \"{signIn.Home}\"\r\n"
            + $"echo {signIn.Hint}\r\ncall {command}{marker}\r\necho.\r\npause\r\n";
    }
}
