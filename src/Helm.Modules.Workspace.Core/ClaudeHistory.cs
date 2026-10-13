using System.Collections.Concurrent;

namespace Helm.Modules.Workspace;

/// <summary>
/// The sessions Claude Code kept for a folder (<c>~/.claude/projects</c>), newest first, each summed up for the
/// phone's History sheet: title, when, how many messages, the first prompt and the last answer. A transcript is read
/// again only when it grew, so asking often is cheap.
/// </summary>
public sealed class ClaudeHistory(string? projectsRoot = null)
{
    private readonly ConcurrentDictionary<string, (long Length, HistoryEntry? Entry)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public const int PreviewLength = 160;

    public string? ProjectsRoot { get; } = projectsRoot;

    /// <summary>The newest sessions of <paramref name="folder"/> that have a conversation in them.</summary>
    public IReadOnlyList<HistoryEntry> For(string folder, int max = WorkspaceSync.MaxHistory)
    {
        var entries = new List<HistoryEntry>();
        // A few more files than needed: empty sessions (opened, never used) are skipped.
        foreach (var (id, path, written) in ClaudeProjects.RecentSessions(folder, DateTimeOffset.MinValue, max + 10, ProjectsRoot))
        {
            if (Summarize(id, path, written) is { } entry) entries.Add(entry);
            if (entries.Count == max) break;
        }
        return entries;
    }

    /// <summary>The transcript of a session of <paramref name="folder"/>, when Claude kept one.</summary>
    public string? TranscriptOf(string folder, string sessionId) =>
        ClaudeProjects.RecentSessions(folder, DateTimeOffset.MinValue, int.MaxValue, ProjectsRoot).FirstOrDefault(s => s.SessionId == sessionId).TranscriptPath;

    private HistoryEntry? Summarize(string id, string path, DateTimeOffset written)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (_cache.TryGetValue(path, out var known) && known.Length == length) return known.Entry is null ? null : known.Entry with { LastAt = written };
        var view = TranscriptReader.ReadView(path, int.MaxValue, withAgents: false);
        var talk = view.Items.Where(i => i.Kind is TranscriptItemKind.User or TranscriptItemKind.Answer).ToList();
        HistoryEntry? entry = null;
        if (talk.Count > 0)
        {
            var first = talk.FirstOrDefault(i => i.Kind == TranscriptItemKind.User)?.Text ?? "";
            entry = new HistoryEntry
            {
                SessionId = id,
                Title = view.Title.Length > 0 ? view.Title : Shorten(first, 60),
                LastAt = written,
                Messages = talk.Count,
                Model = view.Model,
                FirstPrompt = Shorten(first, PreviewLength),
                LastReply = Shorten(talk.LastOrDefault(i => i.Kind == TranscriptItemKind.Answer)?.Text ?? "", PreviewLength),
            };
        }
        _cache[path] = (length, entry);
        return entry;
    }

    internal static string Shorten(string text, int max)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}

/// <summary>
/// Which session each Claude tab of a VS Code window shows. Claude names a tab after its conversation, cut to 25
/// characters ("Claude Code" before it has a title), and tells nothing else; Helm matches those names to the sessions
/// it knows in that window.
/// </summary>
public static class ClaudeTabMatch
{
    public const string Untitled = "Claude Code";

    /// <summary>The name Claude gives a tab for <paramref name="title"/> (the extension's own rule).</summary>
    public static string Label(string title) =>
        title.Length == 0 ? Untitled : title.Length > 25 ? title[..24] + "…" : title;

    /// <summary>A session that could be in a tab.</summary>
    public sealed record Candidate(string SessionId, string Title, string? FirstPrompt, DateTimeOffset Updated);

    /// <summary>
    /// The session of each tab, in tab order (null where none matches), and the active tab's session. A name that
    /// fits several sessions goes to the newest not taken yet; an untitled tab to the newest session without a title.
    /// </summary>
    public static (IReadOnlyList<string?> Sessions, string? Active) Match(IReadOnlyList<(string Label, bool Active)> tabs, IReadOnlyList<Candidate> candidates)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var newest = candidates.OrderByDescending(c => c.Updated).ToList();
        var result = new List<string?>();
        string? active = null;
        foreach (var (label, isActive) in tabs)
        {
            var match = label == Untitled
                ? newest.FirstOrDefault(c => c.Title.Length == 0 && !taken.Contains(c.SessionId))
                : newest.FirstOrDefault(c => !taken.Contains(c.SessionId) && c.Title.Length > 0 && Label(c.Title) == label)
                  ?? newest.FirstOrDefault(c => !taken.Contains(c.SessionId) && c.Title.Length == 0 && c.FirstPrompt is { Length: > 0 } p && Label(p.Trim()) == label);
            if (match is not null) taken.Add(match.SessionId);
            result.Add(match?.SessionId);
            if (isActive) active = match?.SessionId;
        }
        return (result, active);
    }
}
