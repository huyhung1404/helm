using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Shell.BugReports;

/// <summary>Where a bug can be handed to: a new Claude Code session in a VS Code window, or one already open.</summary>
/// <param name="To">What <c>workspace_send</c> takes: a session id, or a window's project.</param>
public sealed record HandOffTarget(string Label, string Detail, string To, bool NewSession, bool Busy, string Project);

/// <summary>
/// "Hand to Claude Code": types a fix request into a Claude Code session on one of the user's PCs through Workspace
/// Controller (its <c>workspace_sessions</c> and <c>workspace_send</c> tools), on PC and phone alike.
/// </summary>
public sealed class BugHandOff(IEnumerable<IMcpToolProvider> providers)
{
    /// <summary>The project whose window a new session opens in by default.</summary>
    public const string DefaultProject = "helm";

    private McpTool? Tool(string name) => providers.SelectMany(p => p.Tools).FirstOrDefault(t => t.Name == name);

    /// <summary>False when Workspace Controller is not part of this Helm.</summary>
    public bool IsAvailable => Tool("workspace_send") is not null;

    /// <summary>The request typed into the session.</summary>
    public static string Prompt(BugReport bug) =>
        $"Fix Helm bug #{bug.Code} (\"{bug.Title}\"): call bug_get {bug.Code} (Helm MCP), claim it with bug_claim, " +
        "and follow docs/bug-fixing.md: a branch, a test that fails first, the fix, then bug_resolve.";

    /// <summary>New sessions in each window first (the helm project's on top), then the open sessions.</summary>
    public async Task<IReadOnlyList<HandOffTarget>> TargetsAsync(CancellationToken ct = default)
    {
        if (Tool("workspace_sessions") is not { } tool) return [];
        using var empty = JsonDocument.Parse("{}");
        var result = await tool.Run(empty.RootElement, ct).ConfigureAwait(false);
        var json = JsonSerializer.SerializeToElement(result);
        var targets = new List<HandOffTarget>();
        if (json.TryGetProperty("windows", out var windows))
        {
            foreach (var w in windows.EnumerateArray())
            {
                var project = Text(w, "project");
                targets.Add(new HandOffTarget($"New session in {project}", Text(w, "pc"), project, true, false, project));
            }
        }
        if (json.TryGetProperty("sessions", out var sessions))
        {
            foreach (var s in sessions.EnumerateArray())
            {
                var state = Text(s, "state");
                var title = Text(s, "title");
                var project = Text(s, "project");
                targets.Add(new HandOffTarget(title.Length > 0 ? $"{project} · \"{title}\"" : project, $"{state} · {Text(s, "pc")}", Text(s, "id"), false,
                    state is "working" or "needspermission" or "question", project));
            }
        }
        return [.. targets.OrderBy(t => t.NewSession && t.Project.Equals(DefaultProject, StringComparison.OrdinalIgnoreCase) ? 0 : t.NewSession ? 1 : t.Busy ? 3 : 2)];
    }

    /// <summary>Sends the request; returns where it went ("Helm · new session · PC-HUNG").</summary>
    /// <exception cref="McpToolException">No PC online, or the target is gone.</exception>
    public async Task<string> SendAsync(HandOffTarget target, string prompt, CancellationToken ct = default)
    {
        var tool = Tool("workspace_send") ?? throw new McpToolException("Workspace Controller is not in this Helm.");
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { to = target.To, text = prompt, new_session = target.NewSession }));
        var result = JsonSerializer.SerializeToElement(await tool.Run(args.RootElement, ct).ConfigureAwait(false));
        return result.TryGetProperty("sent_to", out var to) ? to.GetString() ?? target.Label : target.Label;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
