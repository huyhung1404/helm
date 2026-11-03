using System.Text;

namespace Helm.Ai.Assistant;

/// <summary>
/// What an agent CLI's chat (Claude Code, Codex, Grok) sends next, and a short record of what was already said. What
/// waits to be sent (the user's words, tool results) is only cleared once a turn went through, so a failed turn loses
/// nothing: the next one sends it again, and the model is never left without the results of calls Helm already ran.
/// A new session (a process that died or was stopped, a session the CLI lost) knows nothing: its first message has the
/// whole protocol again (<see cref="CliAgentProtocol.Preamble"/> with every tool so far) and a recap of the conversation.
/// </summary>
public sealed class CliConversation(string system, IReadOnlyList<AssistantTool> tools)
{
    /// <summary>How much of each earlier message a recap keeps, and of all of them (the latest first).</summary>
    internal const int RecapEach = 600, RecapAll = 6000;

    private readonly List<AssistantTool> _tools = [.. tools];
    private readonly List<string> _said = [];
    private readonly StringBuilder _pending = new();
    private readonly List<AssistantImage> _images = [];
    private string _more = "";

    /// <summary>Something waits to be sent.</summary>
    public bool HasPending => _pending.Length > 0 || _images.Count > 0;

    /// <summary>
    /// The photos that go with the next message (<see cref="CliImages"/>), kept like the words until a turn went
    /// through. A recap never has them: a restarted agent only hears that a photo was sent.
    /// </summary>
    public IReadOnlyList<AssistantImage> Images => _images;

    public void AddUser(string text) => Append(text);

    public void AddUser(string text, AssistantImage image)
    {
        if (text.Length > 0) Append(text);
        _images.Add(image);
    }

    public void AddResults(IReadOnlyList<ToolOutcome> results) => Append(CliAgentProtocol.Results(results));

    /// <summary>Tools a later turn adds: a session that has the earlier list is told before the next message.</summary>
    public void AddTools(IReadOnlyList<AssistantTool> added)
    {
        _tools.AddRange(added);
        _more += CliAgentProtocol.MoreTools(added) + "\n";
    }

    /// <summary>
    /// The message for the next turn. A new session gets the preamble with every tool, then (when something was said
    /// before) the recap; one that has it gets only the tools added since and what waits.
    /// </summary>
    public string Message(bool newSession)
    {
        if (!newSession) return _more + _pending;
        var text = new StringBuilder(CliAgentProtocol.Preamble(system, _tools)).Append("\n\n");
        if (_said.Count > 0) text.Append(Recap()).Append("\n\n");
        return text.Append(_pending).ToString();
    }

    /// <summary>The turn went through with this answer: what was sent is said, nothing waits.</summary>
    public void Answered(string answer)
    {
        if (_pending.Length > 0 || _images.Count > 0) _said.Add((_images.Count > 0 ? "(the user sent a photo) " : "") + _pending);
        _said.Add("You: " + answer.Trim());
        _pending.Clear();
        _images.Clear();
        _more = "";
    }

    private void Append(string text)
    {
        if (_pending.Length > 0) _pending.Append("\n\n");
        _pending.Append(text);
    }

    /// <summary>The conversation so far, each message cut short, as much of the latest as fits.</summary>
    private string Recap()
    {
        var lines = new List<string>();
        var length = 0;
        for (var i = _said.Count - 1; i >= 0 && length < RecapAll; i--)
        {
            var line = _said[i].Length > RecapEach ? _said[i][..RecapEach] + "…" : _said[i];
            lines.Insert(0, line);
            length += line.Length;
        }
        return "Helm restarted you in the middle of this conversation. What was said before (shortened, oldest first; "
            + "lines that start with \"You:\" are your own answers, Helm already ran the tools you called):\n"
            + string.Join("\n---\n", lines)
            + "\n\nThe conversation goes on from here:";
    }
}
