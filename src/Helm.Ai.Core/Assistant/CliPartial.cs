namespace Helm.Ai.Assistant;

/// <summary>
/// What an agent CLI's step (Claude Code, Codex, Grok) shows of its answer while it writes it. The step may turn out to
/// be a call list (<see cref="CliAgentProtocol"/>: one JSON object, bare or in a code fence), which the user must never
/// see: text that starts like one is held back. A step that ends as plain text the caller has not seen whole (held
/// back, or a CLI that does not stream) is said whole at its end; one that ends as calls takes back what was said ("").
/// </summary>
public sealed class CliPartial(Action<string>? partial)
{
    private string _said = "";

    /// <summary>The answer's text so far, as the CLI wrote it.</summary>
    public void Show(string soFar)
    {
        if (partial is null) return;
        var text = soFar.TrimStart();
        if (text.Length == 0 || text == _said) return;
        if (text[0] is '{' or '`')
        {
            // A new message that looks like calls: what an earlier one said is taken back.
            Clear();
            return;
        }
        _said = text;
        partial(text);
    }

    /// <summary>The step's end: its text said whole when the caller has not seen it so, or taken back for calls.</summary>
    public void Done(AssistantStep step)
    {
        if (partial is null) return;
        if (step.Calls.Count > 0) Clear();
        else if (step.Text.Length > 0 && step.Text != _said)
        {
            _said = step.Text;
            partial(step.Text);
        }
    }

    private void Clear()
    {
        if (_said.Length == 0) return;
        _said = "";
        partial!("");
    }
}
