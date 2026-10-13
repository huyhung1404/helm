namespace Helm.Modules.Workspace;

public enum NoticeKind
{
    Done,
    Permission,
    Question,
}

/// <summary>One phone notification: a session finished or waits for you. Grouped per VS Code window (a tab).</summary>
public sealed record WorkspaceNotice(
    NoticeKind Kind, string MachineId, string SessionId, string Group, string GroupTitle, string Title, string Text);

/// <summary>
/// Decides what the phone notifies, from one sync of the session records to the next: a session that just finished,
/// and a permission or a question that just started to wait for you. Nothing for what was already so when watching
/// began (no burst of old news), nor for the conversation you are looking at. Not thread-safe; one caller.
/// </summary>
public sealed class WorkspaceNotifier
{
    private readonly Dictionary<string, (SessionState State, string? RequestId)> _last = new(StringComparer.Ordinal);
    private bool _primed;

    public IReadOnlyList<WorkspaceNotice> Next(IReadOnlyCollection<WorkspaceSessionRecord> sessions,
        IReadOnlyCollection<WorkspaceHostRecord> hosts, string? viewingSessionId)
    {
        var notices = new List<WorkspaceNotice>();
        foreach (var r in sessions)
        {
            _last.TryGetValue(r.SessionId, out var before);
            var known = _last.ContainsKey(r.SessionId);
            _last[r.SessionId] = (r.State, r.Pending?.RequestId);
            if (!_primed || !known || r.SessionId == viewingSessionId) continue;

            var host = hosts.FirstOrDefault(h => h.MachineId == r.MachineId);
            var window = host?.Windows.FirstOrDefault(w => SessionRegistry.FolderKey(w.Folder) == SessionRegistry.FolderKey(r.WindowFolder))?.Name;
            var group = r.MachineId + "|" + SessionRegistry.FolderKey(r.WindowFolder);
            var groupTitle = string.Join(" · ", new[] { window, host?.MachineName }.Where(x => !string.IsNullOrEmpty(x)));
            var title = r.Title.Length > 0 ? r.Title : "Claude";

            if (r.Pending is { } p && p.RequestId != before.RequestId)
            {
                notices.Add(p.Kind == PendingKind.Question
                    ? new WorkspaceNotice(NoticeKind.Question, r.MachineId, r.SessionId, group, groupTitle, title,
                        "Asks: " + Shorten(p.Questions.FirstOrDefault()?.Question ?? "a question", 160))
                    : new WorkspaceNotice(NoticeKind.Permission, r.MachineId, r.SessionId, group, groupTitle, title,
                        $"Wants to use {p.Tool}: " + Shorten(p.Detail, 160)));
            }
            else if (r.State == SessionState.Done && before.State != SessionState.Done)
            {
                notices.Add(new WorkspaceNotice(NoticeKind.Done, r.MachineId, r.SessionId, group, groupTitle, title,
                    Shorten(r.LastReply ?? "Done.", 200)));
            }
        }
        foreach (var gone in _last.Keys.Where(k => sessions.All(s => s.SessionId != k)).ToList()) _last.Remove(gone);
        _primed = true;
        return notices;
    }

    private static string Shorten(string text, int max)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
