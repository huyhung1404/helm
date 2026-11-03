using System.Text;

namespace Helm.Ai.Assistant;

/// <summary>
/// A conversation with a provider's model as plain lines, for starting it again from a short recap: when its history
/// grew long (every call sends all of it), or when its tools changed (Claude ties its thinking to the tools it was given:
/// history sent back under other tools is refused). The recap replays no tool call or thinking block, so the new
/// history is valid whatever the provider's pairing rules, and it is the only thing the model keeps of what was before.
/// </summary>
internal sealed class ChatRecap
{
    /// <summary>A history longer than this (characters) starts again from a recap at the next user turn.</summary>
    internal const int MaxHistory = 24_000;

    /// <summary>How much of each line a recap keeps, and of all of them (the latest first).</summary>
    internal const int RecapEach = 1_200, RecapAll = 8_000;

    private readonly List<string> _lines = [];
    private readonly Dictionary<string, string> _calls = new(StringComparer.Ordinal);

    /// <summary>The characters the history holds now (roughly what each call sends again).</summary>
    public int Size { get; private set; }

    public bool TooLong => Size > MaxHistory;

    public void User(string text) => Add("User: " + text);

    public void Answer(string text, IReadOnlyList<ToolCall> calls)
    {
        if (text.Length > 0) Add("You: " + text);
        foreach (var call in calls)
        {
            _calls[call.Id] = call.Name;
            Add($"You called {call.Name} {call.Input.GetRawText()}");
        }
    }

    public void Results(IReadOnlyList<ToolOutcome> results)
    {
        foreach (var result in results)
            Add($"{(_calls.TryGetValue(result.CallId, out var name) ? name : "A tool")} {(result.IsError ? "failed" : "answered")}: {result.Content}");
    }

    /// <summary>
    /// The recap to start again with, put before <paramref name="text"/> (the next user turn), as the first message of
    /// the new history; the record goes on from it.
    /// </summary>
    public string StartAgain(string text)
    {
        var lines = new List<string>();
        var length = 0;
        for (var i = _lines.Count - 1; i >= 0 && length < RecapAll; i--)
        {
            var line = _lines[i].Length > RecapEach ? _lines[i][..RecapEach] + "…" : _lines[i];
            lines.Insert(0, line);
            length += line.Length;
        }
        var recap = new StringBuilder("Earlier in this conversation (shortened, oldest first; Helm already ran the tools you called):\n")
            .AppendJoin('\n', lines).Append("\n\n").Append(text).ToString();
        _lines.Clear();
        _calls.Clear();
        Size = 0;
        Add(recap);
        return recap;
    }

    private void Add(string line)
    {
        _lines.Add(line);
        Size += line.Length;
    }
}
