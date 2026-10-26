namespace Helm.Modules.NovelReader.Reports;

/// <summary>The AI could not suggest translations: not installed, not connected to Helm, stopped with an error.</summary>
public sealed class TranslationAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// An AI on this device that suggests the right translation of reported sentences through Helm's MCP tools
/// (<see cref="TranslationReportMcpTools"/>): it reads the reports and the novel's names and writes its suggestions back,
/// so they arrive in the store like any other change. On Windows this is Claude Code, run without a window; other
/// platforms have none.
/// </summary>
public interface ITranslationAgent
{
    /// <summary>Its name in messages ("Claude Code").</summary>
    string Name { get; }

    /// <summary>Why it cannot run now (not installed, Helm not added to it, MCP off), or null when it can.</summary>
    string? Unavailable();

    /// <summary>Suggests translations for some reports of one novel; returns what the AI said when it was done.</summary>
    /// <exception cref="TranslationAgentException">It could not finish.</exception>
    /// <exception cref="OperationCanceledException">The reader stopped it (the suggestions written so far stay).</exception>
    Task<string> SuggestAsync(string bookId, string title, IReadOnlyList<string> reportIds, IProgress<string>? progress, CancellationToken ct);
}
