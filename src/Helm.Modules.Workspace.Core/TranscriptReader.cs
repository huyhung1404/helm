using System.Text;
using System.Text.Json;

namespace Helm.Modules.Workspace;

/// <summary>One message of a conversation, as the phone shows it.</summary>
public sealed record TranscriptTurn(bool FromUser, string Text, DateTimeOffset? At);

/// <summary>The end of a Claude Code transcript (<c>~/.claude/projects/…/&lt;session&gt;.jsonl</c>).</summary>
public sealed record TranscriptTail(string Title, IReadOnlyList<TranscriptTurn> Turns);

/// <summary>
/// Reads the end of a Claude Code transcript: the conversation's title and its last messages (your prompts and
/// Claude's text, without tool calls, tool output, thinking or side chains). Reads only the tail of a long file.
/// Never throws: a missing or half-written file gives what it can.
/// </summary>
public static class TranscriptReader
{
    /// <summary>How much of the file's end is read.</summary>
    public const int TailBytes = 512 * 1024;

    public static TranscriptTail Read(string path, int maxTurns = 20)
    {
        var lines = ReadTailLines(path, TailBytes, out var partial);
        var title = "";
        var turns = new List<TranscriptTurn>();
        foreach (var line in lines) Parse(line, ref title, turns);
        // Titles are written again and again, so the tail nearly always has one; scan the whole file only if not.
        if (title.Length == 0 && partial)
        {
            var none = new List<TranscriptTurn>();
            foreach (var line in ReadTailLines(path, int.MaxValue, out _)) Parse(line, ref title, none);
        }
        return new TranscriptTail(title, turns.Count > maxTurns ? turns.GetRange(turns.Count - maxTurns, maxTurns) : turns);
    }

    private static void Parse(string line, ref string title, List<TranscriptTurn> turns)
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
                    title = custom;
                    return;
                case "ai-title" when Text(root, "aiTitle") is { Length: > 0 } ai:
                    title = ai;
                    return;
                case "user" or "assistant":
                    break;
                default:
                    return;
            }
            if (Flag(root, "isSidechain") || Flag(root, "isMeta")) return;
            if (!root.TryGetProperty("message", out var message)) return;
            var text = MessageText(message);
            if (string.IsNullOrWhiteSpace(text)) return;
            DateTimeOffset? at = DateTimeOffset.TryParse(Text(root, "timestamp"), out var t) ? t.ToLocalTime() : null;
            var fromUser = type == "user";
            // Claude's text blocks of one answer arrive as separate records: join them.
            if (!fromUser && turns.Count > 0 && !turns[^1].FromUser)
                turns[^1] = turns[^1] with { Text = turns[^1].Text + "\n\n" + text, At = at ?? turns[^1].At };
            else
                turns.Add(new TranscriptTurn(fromUser, text, at));
        }
        catch (JsonException)
        {
            // A line being written right now.
        }
    }

    private static string MessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return Clean(content.GetString() ?? "");
        if (content.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (Text(part, "type") == "text" && Text(part, "text") is { Length: > 0 } text) parts.Add(text);
        }
        return Clean(string.Join("\n\n", parts));
    }

    /// <summary>Commands the CLI echoes into the transcript (/model, local command output) are not conversation.</summary>
    private static string Clean(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("<command-name>", StringComparison.Ordinal) || t.StartsWith("<local-command-", StringComparison.Ordinal)) return "";
        return t;
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
