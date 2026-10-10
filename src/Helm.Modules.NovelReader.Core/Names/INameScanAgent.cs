namespace Helm.Modules.NovelReader.Names;

/// <summary>The AI could not look for names: not installed, not connected to Helm, stopped with an error.</summary>
public sealed class NameAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// An AI on this device that finds a novel's names by itself through Helm's MCP tools (<see cref="NovelReaderMcpTools"/>):
/// it reads the candidates and adds the real names, so they arrive in the store like any other change. On Windows this is
/// Claude Code, run without a window; other platforms have none and use the logic scan.
/// </summary>
public interface INameScanAgent
{
    /// <summary>Its name in messages ("Claude Code").</summary>
    string Name { get; }

    /// <summary>Why it cannot run now (not installed, Helm not added to it, MCP off), or null when it can.</summary>
    string? Unavailable();

    /// <summary>Looks for the names of one novel and adds them; returns what the AI said when it was done.</summary>
    /// <exception cref="NameAgentException">It could not finish.</exception>
    /// <exception cref="OperationCanceledException">The reader stopped it (the names added so far stay).</exception>
    Task<string> FindNamesAsync(string bookId, string title, IProgress<string>? progress, CancellationToken ct);
}
