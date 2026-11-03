namespace Helm.Ai.Assistant;

/// <summary>
/// A web turn (<see cref="WebSearch"/>): always the API, never an agent on a PC (their web tools are off on purpose),
/// on a Claude chat with Claude's server-side web search, at most <see cref="WebSearch.MaxUses"/> searches a call. A
/// Grok pick asks Claude for that turn and the answer says so. From the first search on, the conversation's calls count
/// as <see cref="AiFeatureIds.WebSearch"/>'s (its own daily cap), search fees included.
/// </summary>
public sealed partial class AssistantSession
{
    /// <summary>The open chat has web search.</summary>
    private bool _web;

    /// <summary>The tool Claude calls when a request turns out to need the web; offered once per chat.</summary>
    private AssistantTool? _lookup;

    /// <summary>The feature this conversation's API calls count for.</summary>
    private string CountedAs => _web ? AiFeatureIds.WebSearch : AiFeatureIds.AskHelm;

    private async Task<AssistantReply> OnWebAsync(string text, bool first, bool factCheck, Func<IReadOnlyList<PendingAction>, Task<bool>> confirm,
        CancellationToken ct)
    {
        if (_helm.WebUnavailable() is { } problem) return new AssistantReply("", [], false, problem);
        string? note = null;
        var moved = false;
        if (_open is not { } open || !open.Chat.AddWebSearch(WebSearch.MaxUses))
        {
            // No chat yet, a conversation on a PC, or a provider without web search here: Claude takes it from here.
            var picked = Model;
            var claude = _helm.WebModel();
            if (picked is not null && picked.Provider != AiProviders.Claude)
                note = $"{Short(picked.Name)} không tìm web qua API được nên câu này em hỏi {Short(claude.Name)}.";
            moved = _history.Count > 0;
            _pc?.End(_conversationId);
            _pc = null;
            try
            {
                _open = _helm.Open(_tools, claude);
            }
            catch (AssistantException ex)
            {
                return new AssistantReply("", [], false, ex.Message);
            }
            _lookup = null;
            _open.Value.Chat.AddWebSearch(WebSearch.MaxUses);
        }
        _web = true;
        var (chat, model, keyId) = _open.Value;
        var context = moved ? "Earlier in this conversation:\n" + string.Join("\n", _history.Select(h => $"{h.Role}: {h.Text}")) + "\n\n" : "";
        var turn = WebSearch.Turn(text, factCheck);
        chat.AddUser(first || moved ? $"{_helm.Now()}\n\n{context}{turn}" : turn);
        var outcome = await AssistantLoop.RunAsync(chat, WithLookup(chat, model), confirm, u => _helm.Count(model, keyId, u, CountedAs),
            () => _helm.Unavailable() ?? _helm.WebUnavailable(), phoneHere: true, model.Name, ct);
        var answer = note is null || outcome.Text.Length == 0 ? outcome.Text : $"{note} {outcome.Text}";
        return new AssistantReply(answer, outcome.Done, outcome.Declined, outcome.Problem);
    }

    /// <summary>
    /// The turn's tools, with <see cref="WebSearch.LookupTool"/> on a Claude chat while web search is on but the words
    /// did not ask for it: Claude turns the search on itself when the request needs current facts.
    /// </summary>
    private IReadOnlyList<AssistantTool> WithLookup(IAssistantChat chat, AssistantModelOption model)
    {
        if (_lookup is null && !_web && model.Provider == AiProviders.Claude && _helm.WebChoice.Enabled && _helm.WebUnavailable() is null)
        {
            _lookup = WebSearch.LookupTool(() => _web = chat.AddWebSearch(WebSearch.MaxUses) || _web);
            chat.AddTools([_lookup]);
        }
        return _lookup is null ? _tools : [.. _tools, _lookup];
    }
}
