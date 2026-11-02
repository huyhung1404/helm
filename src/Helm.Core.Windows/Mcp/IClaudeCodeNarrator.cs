using System.Text.Json;

namespace Helm.Core.Mcp;

/// <summary>
/// Tells what Claude Code does, step by step, for a person following a run (<see cref="ClaudeCodeRunner.RunAsync"/>):
/// each tool call with what it was given, each answer, and what Claude says between them. Null skips a step.
/// </summary>
public interface IClaudeCodeNarrator
{
    /// <summary>A tool call: its name (without the MCP prefix), its input, and how many calls so far.</summary>
    string? Call(string tool, JsonElement input, int calls);

    /// <summary>A tool's answer (usually JSON text); <paramref name="isError"/> when the tool refused.</summary>
    string? Result(string tool, string result, bool isError);

    /// <summary>What Claude says in its own words between calls.</summary>
    string? Text(string text);
}
