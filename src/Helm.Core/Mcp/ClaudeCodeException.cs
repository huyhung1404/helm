namespace Helm.Core.Mcp;

/// <summary>
/// Claude Code could not do the job: not installed, not connected to Helm, stopped with an error, too slow. Here, not
/// beside ClaudeCodeRunner (Windows), so portable tool logic (Slack Report's sorting) can catch it.
/// </summary>
public sealed class ClaudeCodeException(string message, Exception? inner = null) : Exception(message, inner);
