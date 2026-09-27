using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Modules.ClaudeChat.Chat;

public enum SuggestionKind
{
    /// <summary>Something Helm does itself (attach, clear, export, switch model).</summary>
    Action,

    /// <summary>A Claude Code slash command, inserted as <c>/name </c> and sent as a message.</summary>
    Command,

    File,
    Folder,
}

/// <summary>Helm's own entries of the "/" menu.</summary>
public enum ComposerAction
{
    None,
    AttachFile,
    MentionFile,
    ClearConversation,
    ExportConversation,
    SwitchModel,
}

/// <summary>One row of the composer's "/" or "@" menu.</summary>
/// <param name="Group">Section header ("Context", "Model", "Commands", "Skills", "Files").</param>
/// <param name="Insert">Text that replaces the typed "/query" or "@query" (for commands and files).</param>
public sealed record Suggestion(SuggestionKind Kind, string Title, string? Detail, string Group, string? Insert = null, ComposerAction Action = ComposerAction.None);

/// <summary>The "/query" or "@query" being typed at the caret.</summary>
public readonly record struct MenuTrigger(char Symbol, int Start, string Query)
{
    public int Length => Query.Length + 1;
}

/// <summary>
/// The composer's "/" and "@" menus, as in Claude Code for VS Code: "/" at the very start of the message lists
/// Helm's actions and Claude Code's commands and skills; "@" after a space (or at the start) lists the chat
/// folder's files. The CLI itself expands "@path" and runs "/command" when they arrive in a message.
/// </summary>
public static class ComposerMenu
{
    private const int MaxFiles = 60;

    private static readonly Suggestion[] s_actions =
    [
        new(SuggestionKind.Action, "Attach file…", "Pick a file to mention in the message", "Context", Action: ComposerAction.AttachFile),
        new(SuggestionKind.Action, "Mention file from this project…", "Type @ to search the folder", "Context", Action: ComposerAction.MentionFile),
        new(SuggestionKind.Action, "Clear conversation", "Start over in this chat (the old one stays in history)", "Context", Action: ComposerAction.ClearConversation),
        new(SuggestionKind.Action, "Export conversation", "Save this chat as a Markdown file", "Context", Action: ComposerAction.ExportConversation),
        new(SuggestionKind.Action, "Switch model…", "Choose the model for this chat", "Model", Action: ComposerAction.SwitchModel),
    ];

    /// <summary>
    /// The trigger being typed, or null: "/" only as the first character with no space typed after it yet;
    /// "@" at the start or after whitespace, up to the caret, with no whitespace in between.
    /// </summary>
    public static MenuTrigger? FindTrigger(string text, int caret)
    {
        if (caret < 1 || caret > text.Length) return null;
        // The word being typed: back from the caret to the previous whitespace (a path may contain '/').
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start == caret) return null;

        var symbol = text[start];
        var query = text.Substring(start + 1, caret - start - 1);
        return symbol switch
        {
            '/' when start == 0 => new MenuTrigger('/', 0, query),
            '@' => new MenuTrigger('@', start, query), // "me@x.com" starts with 'm', so it is not a mention
            _ => null,
        };
    }

    public static IReadOnlyList<Suggestion> SlashItems(string query, IReadOnlyList<SlashCommand> commands)
    {
        var items = s_actions.Where(a => Matches(a.Title, query)).ToList();
        foreach (var group in new[] { true, false })
        {
            items.AddRange(commands
                .Where(c => c.IsBuiltIn == group && (Matches(c.Name, query) || (query.Length > 2 && Matches(c.Description, query))))
                .OrderBy(c => c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new Suggestion(SuggestionKind.Command, "/" + c.Name, Describe(c), group ? "Commands" : "Skills", "/" + c.Name)));
        }
        return items;
    }

    /// <summary>Folder entries end with "/". Paths whose file name starts with the query come first.</summary>
    public static IReadOnlyList<Suggestion> FileItems(string query, IReadOnlyList<string> relativePaths)
    {
        var q = query.Replace('\\', '/');
        return relativePaths
            .Where(p => q.Length == 0 || p.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Name(p).StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles)
            .Select(p => new Suggestion(p.EndsWith('/') ? SuggestionKind.Folder : SuggestionKind.File, Name(p), Parent(p), "Files", "@" + p))
            .ToList();
    }

    /// <summary>Replaces the typed trigger with <paramref name="insert"/> plus a space; returns the new text and caret.</summary>
    public static (string Text, int Caret) Apply(string text, MenuTrigger trigger, string insert)
    {
        // The whole word goes, also the part after the caret ("@src/M|ain" → the picked path).
        var end = Math.Min(text.Length, trigger.Start + trigger.Length);
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var spaceFollows = end < text.Length;
        var replacement = insert.Length == 0 || spaceFollows ? insert : insert + " ";
        var next = string.Concat(text.AsSpan(0, trigger.Start), replacement, text.AsSpan(end));
        var caret = trigger.Start + replacement.Length + (spaceFollows && insert.Length > 0 ? 1 : 0);
        return (next, Math.Min(caret, next.Length));
    }

    private static string Describe(SlashCommand c) =>
        c.ArgumentHint is { } hint ? $"{c.Description}  {hint}".Trim() : c.Description;

    private static bool Matches(string value, string query) =>
        query.Length == 0 || value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return (slash < 0 ? trimmed : trimmed[(slash + 1)..]) + (path.EndsWith('/') ? "/" : string.Empty);
    }

    private static string? Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? null : trimmed[..(slash + 1)];
    }
}

/// <summary>The files of a chat folder for the "@" menu (relative, '/'-separated, folders ending in '/').</summary>
public static class ProjectFiles
{
    private const int Limit = 20000;

    /// <summary>Build output, dependencies and tool caches: never worth mentioning, often huge.</summary>
    private static readonly HashSet<string> s_skip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "Library", "Temp", "Logs", "Build", "Builds",
        "UserSettings", "packages", ".gradle", "dist", "out", "__pycache__", ".venv", "venv", ".wrangler",
    };

    public static IReadOnlyList<string> List(string folder)
    {
        var result = new List<string>();
        if (!Directory.Exists(folder)) return result;
        var root = Path.GetFullPath(folder);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && result.Count < Limit)
        {
            var dir = pending.Pop();
            IEnumerable<string> dirs, files;
            try
            {
                dirs = Directory.EnumerateDirectories(dir).ToList();
                files = Directory.EnumerateFiles(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var d in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (s_skip.Contains(Path.GetFileName(d))) continue;
                result.Add(Relative(root, d) + "/");
                pending.Push(d);
            }
            result.AddRange(files.Select(f => Relative(root, f)));
        }
        return result;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
