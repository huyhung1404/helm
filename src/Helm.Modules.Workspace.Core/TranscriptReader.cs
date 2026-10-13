using System.Text;
using System.Text.Json;

namespace Helm.Modules.Workspace;

/// <summary>One message of a conversation, as the phone shows it.</summary>
public sealed record TranscriptTurn(bool FromUser, string Text, DateTimeOffset? At);

/// <summary>The end of a Claude Code transcript (<c>~/.claude/projects/…/&lt;session&gt;.jsonl</c>).</summary>
public sealed record TranscriptTail(string Title, IReadOnlyList<TranscriptTurn> Turns);

public enum TranscriptItemKind
{
    /// <summary>Your prompt.</summary>
    User,
    /// <summary>"Thought for 4s": Claude thought (the thinking itself is not stored readable).</summary>
    Thought,
    /// <summary>A tool call, with what went in and what came out.</summary>
    Tool,
    /// <summary>Claude's text (Markdown).</summary>
    Answer,
}

/// <summary>One step of a conversation as the phone draws it: the same timeline as Claude's panel in VS Code.</summary>
public sealed record TranscriptItem
{
    public TranscriptItemKind Kind { get; init; }

    /// <summary>The prompt, the answer (Markdown), "Thought for 4s", or a tool's short description.</summary>
    public string Text { get; init; } = "";

    /// <summary>Tool: its name (Bash, Edit, an MCP tool …).</summary>
    public string? Tool { get; init; }

    /// <summary>Tool: what went in (the command, the pattern), shortened; null when the description says it all.</summary>
    public string? Input { get; init; }

    /// <summary>Tool: the start of what came out; null while it runs.</summary>
    public string? Output { get; init; }

    public bool Running { get; init; }
    public bool Failed { get; init; }
    public DateTimeOffset? At { get; init; }

    /// <summary>Tool: Claude's id for the call (pairs it with its result; not shown).</summary>
    public string? CallId { get; init; }
}

/// <summary>A subagent working for the session now (an Agent / Task call without a result yet).</summary>
public sealed record SubagentInfo
{
    /// <summary>Its kind: Explore, general-purpose, a custom agent …</summary>
    public string Type { get; init; } = "";

    /// <summary>The task it was given (the call's description).</summary>
    public string Task { get; init; } = "";

    public DateTimeOffset? Since { get; init; }

    /// <summary>Its latest step ("Grep …"), when its own transcript tells.</summary>
    public string? Doing { get; init; }
}

/// <summary>Everything the phone shows about a session that the transcript knows.</summary>
public sealed record TranscriptView
{
    public string Title { get; init; } = "";
    public IReadOnlyList<TranscriptItem> Items { get; init; } = [];

    /// <summary>The model of the latest answer (an id such as claude-opus-5-5).</summary>
    public string? Model { get; init; }

    /// <summary>The effort of the latest answer (low … max).</summary>
    public string? Effort { get; init; }

    /// <summary>The permission mode of the latest prompt (default, acceptEdits, plan, auto …).</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Tokens of context the latest answer read (input + cache); 0 when unknown.</summary>
    public long ContextTokens { get; init; }

    public IReadOnlyList<SubagentInfo> Agents { get; init; } = [];
}

/// <summary>
/// Reads the end of a Claude Code transcript: the title, and the conversation as a timeline (your prompts, "Thought
/// for Ns", each tool call with its input and the start of its output, Claude's text), plus the model, effort,
/// permission mode, context used and the subagents still running. Side chains (subagents' own steps) stay out of the
/// timeline. Reads only the tail of a long file. Never throws: a missing or half-written file gives what it can.
/// The format is Claude Code's own and changes between versions: unknown records are skipped.
/// </summary>
public static class TranscriptReader
{
    /// <summary>How much of the file's end is read.</summary>
    public const int TailBytes = 512 * 1024;

    /// <summary>Kept per tool output (the phone shows the start, like VS Code's collapsed OUT).</summary>
    public const int MaxOutput = 1200;
    public const int MaxOutputLines = 14;
    public const int MaxInput = 600;

    public static TranscriptTail Read(string path, int maxTurns = 20)
    {
        var view = ReadView(path, int.MaxValue, withAgents: false);
        var turns = new List<TranscriptTurn>();
        foreach (var item in view.Items)
        {
            if (item.Kind == TranscriptItemKind.User) turns.Add(new TranscriptTurn(true, item.Text, item.At));
            else if (item.Kind == TranscriptItemKind.Answer)
            {
                // One answer with tools in between is still one message in the plain view.
                if (turns.Count > 0 && !turns[^1].FromUser) turns[^1] = turns[^1] with { Text = turns[^1].Text + "\n\n" + item.Text, At = item.At ?? turns[^1].At };
                else turns.Add(new TranscriptTurn(false, item.Text, item.At));
            }
        }
        return new TranscriptTail(view.Title, turns.Count > maxTurns ? turns.GetRange(turns.Count - maxTurns, maxTurns) : turns);
    }

    /// <summary>The timeline (its last <paramref name="maxItems"/> steps) and the session's details.</summary>
    public static TranscriptView ReadView(string path, int maxItems = 80, bool withAgents = true)
    {
        var lines = ReadTailLines(path, TailBytes, out var partial);
        var state = new ParseState();
        foreach (var line in lines) Parse(line, state);
        // Titles are written again and again, so the tail nearly always has one; scan the whole file only if not.
        if (state.Title.Length == 0 && partial)
        {
            var whole = new ParseState();
            foreach (var line in ReadTailLines(path, int.MaxValue, out _)) Parse(line, whole);
            state.Title = whole.Title;
        }
        var items = state.Items;
        var agents = withAgents ? RunningAgents(path, items) : [];
        return new TranscriptView
        {
            Title = state.Title,
            Items = items.Count > maxItems ? items.GetRange(items.Count - maxItems, maxItems) : items,
            Model = state.Model,
            Effort = state.Effort,
            PermissionMode = state.Mode,
            ContextTokens = state.Context,
            Agents = agents,
        };
    }

    private sealed class ParseState
    {
        public string Title = "";
        public readonly List<TranscriptItem> Items = [];
        public readonly Dictionary<string, int> Calls = new(StringComparer.Ordinal);
        public string? Model, Effort, Mode;
        public long Context, ThoughtMs;
    }

    private static void Parse(string line, ParseState s)
    {
        if (line.Length == 0 || line[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Text(root, "type");
            switch (type)
            {
                case "custom-title" when Text(root, "customTitle") is { Length: > 0 } custom:
                    s.Title = custom;
                    return;
                case "ai-title" when Text(root, "aiTitle") is { Length: > 0 } ai:
                    s.Title = ai;
                    return;
                case "user" or "assistant":
                    break;
                default:
                    return;
            }
            if (Flag(root, "isSidechain") || Flag(root, "isMeta") || Flag(root, "isCompactSummary") || Flag(root, "isVisibleInTranscriptOnly")) return;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            DateTimeOffset? at = DateTimeOffset.TryParse(Text(root, "timestamp"), out var t) ? t.ToLocalTime() : null;
            if (type == "assistant") ParseAssistant(root, message, at, s);
            else ParseUser(root, message, at, s);
        }
        catch (JsonException)
        {
            // A line being written right now.
        }
    }

    private static void ParseAssistant(JsonElement root, JsonElement message, DateTimeOffset? at, ParseState s)
    {
        if (Text(message, "model") is { Length: > 0 } model && model != "<synthetic>") s.Model = model;
        if (Text(root, "effort") is { Length: > 0 } effort) s.Effort = effort;
        if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var context = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
            if (context > 0) s.Context = context;
        }
        if (root.TryGetProperty("thinkingDurationMs", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var thought))
        {
            // Thinking blocks back to back are one pause in the panel.
            if (s.Items.Count > 0 && s.Items[^1].Kind == TranscriptItemKind.Thought) s.ThoughtMs += thought;
            else
            {
                s.ThoughtMs = thought;
                s.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Thought, At = at });
            }
            s.Items[^1] = s.Items[^1] with { Text = $"Thought for {Math.Max(1, (int)Math.Round(s.ThoughtMs / 1000.0))}s" };
        }
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var part in content.EnumerateArray())
        {
            switch (Text(part, "type"))
            {
                case "text" when Text(part, "text") is { } text && text.Trim().Length > 0:
                    // Claude's text blocks of one answer arrive as separate records: join them.
                    if (s.Items.Count > 0 && s.Items[^1].Kind == TranscriptItemKind.Answer)
                        s.Items[^1] = s.Items[^1] with { Text = s.Items[^1].Text + "\n\n" + text.Trim(), At = at ?? s.Items[^1].At };
                    else
                        s.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = text.Trim(), At = at });
                    break;
                case "tool_use":
                    var name = Text(part, "name") ?? "Tool";
                    var input = part.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                    var (desc, shown) = Describe(name, input);
                    if (Text(part, "id") is { Length: > 0 } id) s.Calls[id] = s.Items.Count;
                    s.Items.Add(new TranscriptItem
                    {
                        Kind = TranscriptItemKind.Tool,
                        Tool = name,
                        Text = desc,
                        Input = shown,
                        Running = true,
                        At = at,
                        CallId = Text(part, "id"),
                    });
                    break;
            }
        }
    }

    private static void ParseUser(JsonElement root, JsonElement message, DateTimeOffset? at, ParseState s)
    {
        if (Text(root, "permissionMode") is { Length: > 0 } mode) s.Mode = mode;
        if (!message.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            AddPrompt(root, content.GetString() ?? "", at, s);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;
        var texts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            switch (Text(part, "type"))
            {
                case "tool_result" when Text(part, "tool_use_id") is { } id && s.Calls.TryGetValue(id, out var index):
                    var call = s.Items[index];
                    s.Items[index] = call with
                    {
                        Running = false,
                        Failed = part.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
                        Output = ResultText(call.Tool ?? "", part),
                    };
                    break;
                case "text" when Text(part, "text") is { } text:
                    texts.Add(text);
                    break;
                case "image":
                    texts.Add("[Image]");
                    break;
            }
        }
        if (texts.Count > 0) AddPrompt(root, string.Join("\n\n", texts), at, s);
    }

    private static void AddPrompt(JsonElement root, string text, DateTimeOffset? at, ParseState s)
    {
        // Background-task notices and the like come as "user" records too; only what a person typed is a prompt.
        if (root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object && Text(origin, "kind") is { } kind && kind != "human") return;
        var clean = Clean(text);
        if (clean.Length > 0) s.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.User, Text = clean, At = at });
    }

    /// <summary>Commands the CLI echoes into the transcript (/model, local command output) are not conversation.</summary>
    private static string Clean(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("<command-name>", StringComparison.Ordinal) || t.StartsWith("<local-command-", StringComparison.Ordinal)
            || t.StartsWith("<task-notification>", StringComparison.Ordinal) || t.StartsWith("<system-reminder>", StringComparison.Ordinal)
            || t.StartsWith("[Request interrupted", StringComparison.Ordinal)) return "";
        return t;
    }

    /// <summary>The words on a tool's line, and what its IN shows (null when the line says it all).</summary>
    internal static (string Description, string? Input) Describe(string tool, JsonElement input)
    {
        string? S(string name) => input.ValueKind == JsonValueKind.Object ? Text(input, name) : null;
        var description = S("description");
        switch (tool)
        {
            case "Bash" or "PowerShell":
                return (description ?? "", Cut(S("command"), MaxInput));
            case "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit":
                return (S("file_path") ?? S("notebook_path") ?? "", null);
            case "Grep":
                return (description ?? "", Cut(GrepLine(S("pattern"), S("path") ?? S("glob")), MaxInput));
            case "Glob":
                return (S("pattern") ?? "", null);
            case "WebFetch":
                return (S("url") ?? "", null);
            case "WebSearch":
                return (S("query") ?? "", null);
            case "Agent" or "Task":
                return (Pair(S("subagent_type"), description) ?? "", null);
            case "Skill":
                return (S("skill") ?? S("command") ?? "", null);
            case "TodoWrite":
                return ("Update the to-do list", null);
            case "ExitPlanMode":
                return ("Present the plan", null);
            case "AskUserQuestion":
                return ("A question for you", null);
        }
        if (description is not null) return (description, null);
        // An MCP tool or one Helm does not know: its input as it was given.
        var raw = input.ValueKind == JsonValueKind.Object ? input.GetRawText() : null;
        return ("", raw is null || raw == "{}" ? null : Cut(raw, MaxInput));
    }

    private static string? Pair(string? a, string? b) => a is null ? b : b is null ? a : a + " · " + b;

    private static string? GrepLine(string? pattern, string? where) =>
        pattern is null ? null : where is null ? $"grep \"{pattern}\"" : $"grep \"{pattern}\" {where}";

    private static string? ResultText(string tool, JsonElement part)
    {
        string text;
        if (!part.TryGetProperty("content", out var content)) text = "";
        else if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
        else if (content.ValueKind == JsonValueKind.Array)
            text = string.Join("\n", content.EnumerateArray().Select(c => Text(c, "type") == "image" ? "[Image]" : Text(c, "text") ?? ""));
        else text = "";
        text = text.Trim();
        // A file's whole text is noise on a phone: how much was read says enough (as VS Code shows it).
        if (tool == "Read" && !text.StartsWith("<", StringComparison.Ordinal))
        {
            var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
            return lines == 1 ? "1 line" : $"{lines} lines";
        }
        return Clip(text);
    }

    /// <summary>The start of an output: a few lines, never too long.</summary>
    private static string Clip(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var head = lines.Length > MaxOutputLines ? string.Join("\n", lines.Take(MaxOutputLines)) + $"\n… {lines.Length - MaxOutputLines} more lines" : text.Replace("\r\n", "\n");
        return Cut(head, MaxOutput) ?? "";
    }

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// The Agent / Task calls still running, with what their subagent does now. A subagent writes its own transcript
    /// (<c>&lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl</c>) with a <c>.meta.json</c> naming the call it serves.
    /// </summary>
    private static IReadOnlyList<SubagentInfo> RunningAgents(string path, List<TranscriptItem> items)
    {
        var running = items.Where(i => i.Kind == TranscriptItemKind.Tool && i.Running && i.Tool is "Agent" or "Task").ToList();
        if (running.Count == 0) return [];
        var folder = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path), "subagents");
        var byCall = new Dictionary<string, (string Type, string Task, string Transcript)>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var meta in Directory.EnumerateFiles(folder, "agent-*.meta.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(meta));
                        if (Text(doc.RootElement, "toolUseId") is not { } call) continue;
                        byCall[call] = (Text(doc.RootElement, "agentType") ?? "", Text(doc.RootElement, "description") ?? "",
                            meta[..^".meta.json".Length] + ".jsonl");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return running.Select(call =>
        {
            var parts = call.Text.Split(" · ", 2);
            var info = new SubagentInfo { Type = parts[0], Task = parts.Length > 1 ? parts[1] : "", Since = call.At };
            if (call.CallId is { } id && byCall.TryGetValue(id, out var known))
                info = info with
                {
                    Type = known.Type.Length > 0 ? known.Type : info.Type,
                    Task = known.Task.Length > 0 ? known.Task : info.Task,
                    Doing = LastStep(known.Transcript),
                };
            return info;
        }).ToList();
    }

    /// <summary>A subagent's latest tool call, as "Grep pattern" / "Bash description".</summary>
    private static string? LastStep(string transcript)
    {
        string? last = null;
        foreach (var line in ReadTailLines(transcript, 64 * 1024, out _))
        {
            if (!line.Contains("\"tool_use\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in c.EnumerateArray().Where(p => Text(p, "type") == "tool_use"))
                {
                    var name = Text(part, "name") ?? "";
                    var (desc, input) = Describe(name, part.TryGetProperty("input", out var i) ? i : default);
                    var what = desc.Length > 0 ? desc : input ?? "";
                    last = Cut(what.Length > 0 ? $"{name} {what}" : name, 160);
                }
            }
            catch (JsonException)
            {
            }
        }
        return last;
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> ReadTailLines(string path, int bytes, out bool partial)
    {
        partial = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - bytes);
            partial = start > 0;
            stream.Position = start;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            // The first line of a tail starts in the middle of a record.
            if (partial) reader.ReadLine();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
