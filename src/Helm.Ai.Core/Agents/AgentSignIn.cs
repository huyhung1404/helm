namespace Helm.Ai.Agents;

/// <summary>
/// Signing an agent in: its CLI's own login, in a terminal the user sees, with nothing but the agent's own folder as its
/// home (<paramref name="HomeVariable"/> = <paramref name="Home"/>), never the user's own login of that CLI.
/// </summary>
/// <param name="Name">The agent as the user named it.</param>
/// <param name="Hint">One line for the user before the CLI starts.</param>
/// <param name="Command">The CLI (as a terminal finds it, or its full path) and its arguments.</param>
/// <param name="Marker">
/// A file to write once the command succeeded (Grok's CLI keeps no sign-in file Helm can read), or null.
/// </param>
public sealed record AgentSignIn(string Name, string Home, string HomeVariable, string Hint, IReadOnlyList<string> Command, string? Marker);

/// <summary>
/// Opens an <see cref="AgentSignIn"/>: a console window of its own on Windows (Helm.App), the current terminal for
/// <c>helm</c> on Linux.
/// </summary>
public interface IAgentSignIn
{
    void Open(AgentSignIn signIn);
}
