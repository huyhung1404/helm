using Helm.Core.Mcp;

namespace Helm.Modules.SlackBot;

/// <summary>Claude Code with no tools, no MCP and no hooks (<see cref="ClaudeCodeRunner.AskAsync"/>).</summary>
public sealed class ClaudeCodeSlackAgent(ClaudeCodeRunner claude) : ISlackAgent
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public string? Unavailable() => ClaudeCodeRunner.AskUnavailable();

    public Task<string> AskAsync(string instructions, string? model, CancellationToken ct) => claude.AskAsync(instructions, model, Timeout, ct);

    /// <summary>Looking into a project takes longer.</summary>
    public static readonly TimeSpan FolderTimeout = TimeSpan.FromMinutes(10);

    public Task<string> AskInFolderAsync(string instructions, IReadOnlyList<string> folders, string? model, CancellationToken ct) =>
        claude.AskInFolderAsync(instructions, folders, model, FolderTimeout, ct);

    /// <summary>A task (a document, a change) takes longer still.</summary>
    public static readonly TimeSpan WorkTimeout = TimeSpan.FromMinutes(20);

    public Task<string> WorkInFolderAsync(string instructions, IReadOnlyList<string> folders, string? model, CancellationToken ct) =>
        claude.WorkInFolderAsync(instructions, folders, model, WorkTimeout, ct);

    public Task<string> AskAsync(string instructions, (string? Model, string? Effort) ai, CancellationToken ct) =>
        claude.AskAsync(instructions, ai.Model, Timeout, ct, ai.Effort);

    public Task<string> AskInFolderAsync(string instructions, IReadOnlyList<string> folders, (string? Model, string? Effort) ai, CancellationToken ct) =>
        claude.AskInFolderAsync(instructions, folders, ai.Model, FolderTimeout, ct, ai.Effort);

    public Task<string> WorkInFolderAsync(string instructions, IReadOnlyList<string> folders, (string? Model, string? Effort) ai, CancellationToken ct) =>
        claude.WorkInFolderAsync(instructions, folders, ai.Model, WorkTimeout, ct, ai.Effort);
}
