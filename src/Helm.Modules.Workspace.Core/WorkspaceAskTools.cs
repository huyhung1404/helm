using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Sync;
using Helm.Core.Text;

namespace Helm.Modules.Workspace;

/// <summary>
/// Claude Code sessions on the user's PCs for Ask Helm: list them, and type a request into one (or into a new one in
/// a VS Code window), the way the phone's Workspace page does: a <see cref="WorkspaceCommand"/> the PC runs. It costs no
/// API: Claude Code works on the user's own plan. Undo withdraws a request the PC has not taken yet.
/// </summary>
public sealed class WorkspaceAskTools(ISyncedCollection<WorkspaceHostRecord> hosts, ISyncedCollection<WorkspaceSessionRecord> sessions,
    ISyncedCollection<WorkspaceCommand> commands, ISyncedCollection<WorkspaceResultRecord> results, ISyncService? sync = null, TimeProvider? time = null)
    : IMcpToolProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string? ModuleId => WorkspaceIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("workspace_sessions", "The Claude Code sessions open on the user's PCs (Workspace Controller): each one's id, title, project folder, VS Code window, PC and state; and the windows a new session can open in.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(List()))
        {
            ReadOnly = true,
            Brief = "Claude Code sessions open on the user's PCs (id, title, project, folder, PC, state), and the windows a new session can open in.",
        },
        new("workspace_send",
            "Type the user's request into a Claude Code session on their PC (free: it runs on their own plan). Name the session by its project, folder, window or title as the user said it (\"Helm\"), or give its id; when several fit, nothing is sent and the answer lists them: ask the user which one. A window with no session, or new_session, opens a new session there.",
            McpArgs.Schema(
                ("to", McpArgs.Text("The session or window: a project or folder name, a title, or a session id from workspace_sessions."), true),
                ("text", McpArgs.Text("The request, as the user said it (tidied, never changed in meaning)."), true),
                ("new_session", McpArgs.Flag("Open a new session in that window instead of typing into one."), false)),
            (args, _) => Task.FromResult<object?>(Send(args)))
        {
            Undo = (result, _) => { Withdraw(result); return Task.CompletedTask; },
            Brief = "Type the user's request into a Claude Code session on their PC (free). to: its project, folder, window or title as the user " +
                "said it, or its id; when several fit, nothing is sent and the answer lists them: ask which. A window with no session, or new_session, opens one.",
            BriefArgs = new Dictionary<string, string> { ["text"] = "as the user said it, tidied, never changed in meaning" },
            BriefResult = McpTool.Keep("sent_to", "new_session"),
        },
    ];

    private sealed record Target(WorkspaceHostRecord Host, HostWindowInfo? Window, WorkspaceSessionRecord? Session)
    {
        public string Folder => Session?.Folder ?? Window!.Folder;
        public string Project => Window?.Name is { Length: > 0 } name ? name : FolderName(Folder);
        public string Title => Session?.Title is { Length: > 0 } title ? title : "";

        /// <summary>"Helm · Fix the sync (working) · PC-HUNG".</summary>
        public string Label => string.Join(" · ", new[] { Project, Title.Length > 0 ? $"{Title} ({State})" : State, Host.MachineName }.Where(p => p.Length > 0));

        private string State => Session is null ? "new session" : Session.State.ToString().ToLowerInvariant();
    }

    private object List()
    {
        var (open, windows) = Targets();
        return new
        {
            sessions = open.Select(t => new
            {
                id = t.Session!.SessionId,
                title = t.Title,
                project = t.Project,
                folder = t.Folder,
                pc = t.Host.MachineName,
                state = t.Session.State.ToString().ToLowerInvariant(),
                waiting = t.Session.Waiting,
                last_prompt = WorkspaceSync.Cut(t.Session.LastPrompt, 120),
            }).ToList(),
            windows = windows.Select(t => new { project = t.Project, folder = t.Folder, pc = t.Host.MachineName }).ToList(),
        };
    }

    private object Send(JsonElement args)
    {
        var to = McpArgs.RequiredString(args, "to").Trim();
        var text = McpArgs.RequiredString(args, "text").Trim();
        var fresh = McpArgs.Bool(args, "new_session") == true;
        var (open, windows) = Targets();
        if (open.Count == 0 && windows.Count == 0)
            throw new McpToolException("No PC with Workspace Controller is online: Helm must run on the PC with Workspace Controller on.");

        Target target;
        var byId = open.FirstOrDefault(t => t.Session!.SessionId == to);
        List<Target> sessionsFit = byId is not null ? [byId] : Fit(open, to);
        if (!fresh && sessionsFit.Count == 1) target = sessionsFit[0];
        else if (!fresh && sessionsFit.Count > 1)
            throw new McpToolException($"Several sessions fit {to}: {string.Join("; ", sessionsFit.Take(6).Select(t => $"{t.Label} [id {t.Session!.SessionId}]"))}. Ask the user which one.");
        else
        {
            // A new session: in the window named, or the window of the session named.
            var windowsFit = Fit(windows, to);
            if (windowsFit.Count == 0 && sessionsFit.Count > 0)
                windowsFit = [.. sessionsFit.Select(s => windows.FirstOrDefault(w => w.Host.MachineId == s.Host.MachineId && Same(w.Folder, s.Session!.WindowFolder))).OfType<Target>().DistinctBy(w => (w.Host.MachineId, SessionRegistry.FolderKey(w.Folder)))];
            target = windowsFit.Count switch
            {
                1 => windowsFit[0],
                0 => throw new McpToolException($"No Claude Code session or VS Code window fits {to}. Open: {string.Join("; ", open.Concat(windows).Take(8).Select(t => t.Label))}."),
                _ => throw new McpToolException($"Several windows fit {to}: {string.Join("; ", windowsFit.Take(6).Select(t => t.Label))}. Ask the user which one."),
            };
        }

        var command = new WorkspaceCommand
        {
            MachineId = target.Host.MachineId,
            Kind = target.Session is null ? WorkspaceCommandKind.NewSession : WorkspaceCommandKind.Prompt,
            SessionId = target.Session?.SessionId,
            Folder = target.Session?.Folder ?? target.Window!.Folder,
            Text = WorkspaceSync.Cut(text)!,
            CreatedAt = _time.GetLocalNow(),
        };
        commands.Upsert(command.Id, command);
        sync?.RequestSyncSoon();
        return new
        {
            id = command.Id,
            sent_to = target.Label,
            session_id = command.SessionId,
            new_session = target.Session is null,
            pc = target.Host.MachineName,
        };
    }

    /// <summary>Undo: the request goes while its PC has not taken it; after that Claude Code has it.</summary>
    private void Withdraw(JsonElement result)
    {
        var id = result.TryGetProperty("id", out var value) ? value.GetString() : null;
        if (id is null || commands.Get(id) is null) return;
        if (results.Get(id) is not null) throw new McpToolException("Claude Code already got it: it cannot be taken back.");
        commands.Delete(id);
        sync?.RequestSyncSoon();
    }

    /// <summary>The sessions a request can go to, and every window of the PCs online (where a new session opens).</summary>
    private (List<Target> Sessions, List<Target> Windows) Targets()
    {
        var now = _time.GetUtcNow();
        var online = hosts.All().Select(h => h.Value).Where(h => h.IsOnline(now)).ToDictionary(h => h.MachineId, StringComparer.Ordinal);
        var open = sessions.All().Select(s => s.Value)
            // Outside VS Code only what Helm already runs there for the phone (taking over a terminal is asked on its page).
            .Where(s => online.ContainsKey(s.MachineId) && s.State != SessionState.Ended && (!s.IsOutside || s.Hosted))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new Target(online[s.MachineId], online[s.MachineId].Windows.FirstOrDefault(w => Same(w.Folder, s.WindowFolder)), s))
            .ToList();
        var windows = online.Values.SelectMany(h => h.Windows.Where(w => w.Connected && w.Folder.Length > 0).Select(w => new Target(h, w, null))).ToList();
        return (open, windows);
    }

    /// <summary>The targets whose project, folder, title or PC hold every word of <paramref name="to"/>, the best first.</summary>
    private static List<Target> Fit(List<Target> targets, string to)
    {
        var terms = TextSearch.Terms(to);
        if (terms.Count == 0) return [];
        var scored = targets.Select(t => (Target: t, Score: TextSearch.Score(terms, t.Project, $"{FolderName(t.Folder)} {t.Title} {t.Host.MachineName}")))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        // The project named exactly beats one that only contains the words ("Helm" over "Helm-develop").
        var exact = scored.Where(x => TextSearch.Fold(x.Target.Project) == TextSearch.Fold(to) || TextSearch.Fold(FolderName(x.Target.Folder)) == TextSearch.Fold(to)).ToList();
        return [.. (exact.Count > 0 ? exact : scored).Select(x => x.Target)];
    }

    private static bool Same(string a, string b) => a.Length > 0 && b.Length > 0 && SessionRegistry.FolderKey(a) == SessionRegistry.FolderKey(b);

    /// <summary>The last part of a PC's path, on any device ("C:\Project\Helm" → "Helm").</summary>
    private static string FolderName(string folder) => folder.Split('/', '\\').LastOrDefault(p => p.Length > 0) ?? folder;
}
