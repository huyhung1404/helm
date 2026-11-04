using Helm.Core.Processes;

namespace Helm.Ai.Agents;

/// <summary>Where each agent's CLI is installed on this machine, as the agents themselves look for it.</summary>
public static class AgentCommands
{
    /// <summary>The CLI an agent of <paramref name="kind"/> runs (<see cref="AgentKinds"/>), or null when it is not installed.</summary>
    public static string? Of(string kind) => kind switch
    {
        AgentKinds.ClaudeCode => CommandFinder.FindClaude(),
        AgentKinds.Grok => GrokAgentFactory.FindGrok(),
        _ => CodexAgentFactory.FindCodex(),
    };

    /// <summary>
    /// A CLI on Linux: on PATH, else where npm (a user prefix) or a native installer puts it in the user's home. A
    /// service's PATH is short, so these are looked at too.
    /// </summary>
    internal static string? Find(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return CommandFinder.OnPath(name)
            ?? CommandFinder.FirstExecutable(Path.Combine(home, ".local", "bin", name), Path.Combine(home, ".npm-global", "bin", name),
                Path.Combine("/usr", "local", "bin", name));
    }
}
