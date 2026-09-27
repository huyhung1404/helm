using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helm.Modules.ClaudeChat.Cli;

/// <summary>A past conversation Claude Code saved for a folder.</summary>
/// <param name="WorkingDirectory">The folder it ran in (from the file's own records); resuming must happen there.</param>
public sealed record PastSession(string SessionId, string Title, DateTime LastActive, string FilePath, string? WorkingDirectory = null)
{
    /// <summary>What a screen reader says for the row: title, folder and date.</summary>
    public string AccessibleName => string.IsNullOrEmpty(FolderName) ? $"{Title}, {LastActive:g}" : $"{Title}, in {FolderName}, {LastActive:g}";

    /// <summary>The folder's last segment, for compact lists.</summary>
    public string FolderName => string.IsNullOrEmpty(WorkingDirectory) ? string.Empty : Path.GetFileName(Path.TrimEndingDirectorySeparator(WorkingDirectory)) is { Length: > 0 } name ? name : WorkingDirectory;
}

/// <summary>One row of a saved conversation, enough to show it again.</summary>
public abstract record TranscriptEntry;

public sealed record TranscriptUserText(string Text) : TranscriptEntry;

public sealed record TranscriptAssistantText(string Text) : TranscriptEntry;

public sealed record TranscriptToolUse(string Id, string Name, JsonElement Input) : TranscriptEntry;

public sealed record TranscriptToolResult(string ToolUseId, string Content, bool IsError) : TranscriptEntry;

/// <summary>
/// Reads the conversations Claude Code keeps in <c>~/.claude/projects/&lt;folder with every non-alphanumeric
/// character replaced by '-'&gt;/&lt;session id&gt;.jsonl</c>. That layout is the CLI's own and undocumented, so everything
/// here is best effort: unreadable files and unknown lines are skipped, nothing throws.
/// </summary>
public static partial class ClaudeSessionStore
{
    private const int TitleLength = 80;

    public static string DefaultProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>The CLI's folder name for <paramref name="workingDirectory"/>, e.g. C:\Project\Helm → C--Project-Helm.</summary>
    public static string EncodeFolder(string workingDirectory) =>
        NonAlphanumeric().Replace(Path.TrimEndingDirectorySeparator(workingDirectory), "-");

    /// <summary>The CLI's data folder for <paramref name="workingDirectory"/> (matched case-insensitively), or null.</summary>
    public static string? FindProjectDirectory(string workingDirectory, string? projectsRoot = null)
    {
        projectsRoot ??= DefaultProjectsRoot;
        if (!Directory.Exists(projectsRoot)) return null;
        var name = EncodeFolder(workingDirectory);
        try
        {
            return Directory.EnumerateDirectories(projectsRoot)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The most recent conversations for <paramref name="workingDirectory"/>, newest first.</summary>
    public static IReadOnlyList<PastSession> List(string workingDirectory, int max = 15, string? projectsRoot = null)
    {
        if (FindProjectDirectory(workingDirectory, projectsRoot) is not { } dir) return [];
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(max * 2).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Summarize(files, max);
    }

    /// <summary>One saved conversation by id (to reopen the chats that were open last time), or null.</summary>
    public static PastSession? Find(string workingDirectory, string sessionId, string? projectsRoot = null)
    {
        if (FindProjectDirectory(workingDirectory, projectsRoot) is not { } dir) return null;
        var file = new FileInfo(Path.Combine(dir, sessionId + ".jsonl"));
        if (!file.Exists) return null;
        var (title, folder) = ReadSummary(file.FullName);
        return new PastSession(sessionId, title ?? "Chat", file.LastWriteTime, file.FullName, folder ?? workingDirectory);
    }

    /// <summary>The most recent conversations across every folder, newest first.</summary>
    public static IReadOnlyList<PastSession> ListRecent(int max = 30, string? projectsRoot = null)
    {
        projectsRoot ??= DefaultProjectsRoot;
        if (!Directory.Exists(projectsRoot)) return [];
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(projectsRoot).EnumerateDirectories()
                .SelectMany(d => SafeFiles(d))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max * 2)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return Summarize(files, max);
    }

    private static IEnumerable<FileInfo> SafeFiles(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateFiles("*.jsonl").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<PastSession> Summarize(IEnumerable<FileInfo> files, int max)
    {
        var sessions = new List<PastSession>();
        foreach (var file in files)
        {
            var (title, folder) = ReadSummary(file.FullName);
            if (title is null) continue; // nothing the user said: not worth listing
            sessions.Add(new PastSession(Path.GetFileNameWithoutExtension(file.Name), title, file.LastWriteTime, file.FullName, folder));
            if (sessions.Count == max) break;
        }
        return sessions;
    }

    /// <summary>
    /// Title: the CLI's generated one (the last wins), else the first thing the user typed; null if neither.
    /// Folder: the <c>cwd</c> of the first record that has one. Only lines that can matter are parsed, since
    /// long conversations run to megabytes.
    /// </summary>
    internal static (string? Title, string? Folder) ReadSummary(string filePath)
    {
        string? aiTitle = null, firstPrompt = null, folder = null;
        if (Open(filePath) is not { } stream) return (null, null);
        foreach (var line in ReadAll(stream))
        {
            var isTitle = line.Contains("\"ai-title\"", StringComparison.Ordinal);
            var wantPrompt = firstPrompt is null && line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
            var wantFolder = folder is null && line.Contains("\"cwd\"", StringComparison.Ordinal);
            if (!isTitle && !wantPrompt && !wantFolder) continue;
            if (Parse(line) is not { } root) continue;

            if (folder is null && Str(root, "cwd") is { Length: > 0 } cwd) folder = cwd;
            switch (Str(root, "type"))
            {
                case "ai-title" when Str(root, "aiTitle") is { Length: > 0 } t:
                    aiTitle = t;
                    break;
                case "user" when firstPrompt is null && IsMainThread(root):
                    firstPrompt = UserText(root);
                    break;
            }
        }
        var title = aiTitle ?? firstPrompt;
        if (title is null) return (null, folder);
        title = title.ReplaceLineEndings(" ").Trim();
        return (title.Length <= TitleLength ? title : title[..TitleLength] + "…", folder);
    }

    internal static string? ReadTitle(string filePath) => ReadSummary(filePath).Title;

    /// <summary>The main conversation (no subagents, no hidden meta messages) in order.</summary>
    public static IReadOnlyList<TranscriptEntry> ReadTranscript(string filePath)
    {
        var entries = new List<TranscriptEntry>();
        foreach (var root in ReadLines(filePath))
        {
            var type = Str(root, "type");
            if (type is not ("user" or "assistant") || !IsMainThread(root) || !root.TryGetProperty("message", out var message)) continue;
            if (!message.TryGetProperty("content", out var content)) continue;

            if (type == "user")
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    if (UserText(root) is { } text) entries.Add(new TranscriptUserText(text));
                    continue;
                }
                if (content.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                {
                    switch (Str(block, "type"))
                    {
                        case "text" when Str(block, "text") is { } t && IsTypedByUser(t):
                            entries.Add(new TranscriptUserText(t));
                            break;
                        case "tool_result":
                            entries.Add(new TranscriptToolResult(
                                Str(block, "tool_use_id") ?? string.Empty,
                                block.TryGetProperty("content", out var c) ? FlattenText(c) : string.Empty,
                                block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True));
                            break;
                    }
                }
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    switch (Str(block, "type"))
                    {
                        case "text" when Str(block, "text") is { Length: > 0 } t:
                            entries.Add(new TranscriptAssistantText(t));
                            break;
                        case "tool_use":
                            entries.Add(new TranscriptToolUse(
                                Str(block, "id") ?? string.Empty,
                                Str(block, "name") ?? string.Empty,
                                block.TryGetProperty("input", out var input) ? input.Clone() : default));
                            break;
                    }
                }
            }
        }
        return entries;
    }

    private static IEnumerable<JsonElement> ReadLines(string filePath)
    {
        if (Open(filePath) is not { } stream) yield break;
        foreach (var line in ReadAll(stream))
        {
            if (Parse(line) is { } root) yield return root;
        }
    }

    private static JsonElement? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FileStream? Open(string filePath)
    {
        try
        {
            // FileShare.ReadWrite: the CLI may be appending to the file right now.
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadAll(Stream stream)
    {
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0) yield return line;
        }
    }

    private static bool IsMainThread(JsonElement root) =>
        !(root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
        && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True);

    /// <summary>What the user typed in a user line, skipping CLI-injected text (command wrappers, reminders).</summary>
    private static string? UserText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() is { } s && IsTypedByUser(s) ? s : null;
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") == "text" && Str(block, "text") is { } t && IsTypedByUser(t)) return t;
        }
        return null;
    }

    /// <summary>Text the CLI inserts itself starts with a tag (&lt;command-name&gt;, &lt;system-reminder&gt;...) or a bracket notice.</summary>
    private static bool IsTypedByUser(string text)
    {
        var t = text.TrimStart();
        return t.Length > 0 && !t.StartsWith('<') && !t.StartsWith("[Request interrupted", StringComparison.Ordinal);
    }

    private static string FlattenText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Join("\n", content.EnumerateArray().Select(b => Str(b, "type") == "text" ? Str(b, "text") : null).Where(t => t is not null));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
