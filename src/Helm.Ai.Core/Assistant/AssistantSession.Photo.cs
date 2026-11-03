namespace Helm.Ai.Assistant;

/// <summary>
/// A photo turn (<see cref="AssistantPhotos"/>): an agent on a Helm PC that reads pictures first (free; the photo
/// travels as an encrypted blob, <see cref="IAgentJobPhotos"/>), the API last, as for words. Either way in a chat of its
/// own that is dropped at the end of the turn, so the photo is never kept; the conversation goes on from what was said
/// and done. When the picked model cannot read pictures, Sonnet reads it once on the API instead and the answer says so.
/// </summary>
public sealed partial class AssistantSession
{
    /// <summary>The model that read the last photo (Sonnet when the picked one could not); null before any.</summary>
    public AssistantModelOption? PhotoModel { get; private set; }

    /// <summary>Where the photo turns the PC's chat of this conversation has not heard of begin in the history; null: none.</summary>
    private int? _untold;

    /// <param name="said">What the user said or typed with it; empty for the photo alone.</param>
    public async Task<AssistantReply> SendPhotoAsync(AssistantImage photo, string said, Func<IReadOnlyList<PendingAction>, Task<bool>> confirm,
        CancellationToken ct)
    {
        if (_helm.Unavailable() is { } problem) return new AssistantReply("", [], false, problem);
        var choice = _helm.Choice;
        said = said.Trim();
        var first = _history.Count == 0;
        _all ??= _helm.OfferedTools(_phone);
        _scope.UnionWith(AssistantPhotos.Groups);
        // The photo says what it is about: words naming no group add nothing.
        _tools = ToolScope.Pick(_all, _scope, said, known: true);

        var text = AssistantPhotos.Prompt(said);
        if (first && _context is { } context) text = context.Before(text);
        // Earlier turns as words: the photo's chat is new and goes when the turn ends.
        var earlier = _history.Count > 0 ? "Earlier in this conversation:\n" + string.Join("\n", _history.Select(h => $"{h.Role}: {h.Text}")) + "\n\n" : "";
        var message = $"{_helm.Now()}\n\n{earlier}{text}";

        // A PC agent that reads pictures first: its chat of its own ends with the turn.
        foreach (var route in _helm.Router.Agents(choice).Where(r => r.ReadsPhotos))
        {
            if (await OnPcAsync(route, message, Model!, choice, ct, $"{_conversationId}-photo-{Guid.NewGuid():N}", [photo]) is { } onPc)
            {
                Said(said, onPc.Text, onPc.Done.Select(d => d.Summary));
                return onPc;
            }
        }
        if (!AiRouter.MayUseApi(choice))
            return new AssistantReply("", [], false,
                "No Helm PC agent that reads photos can take it now (Codex or Claude Code: Grok's CLI takes none), and Ask Helm is set to run on a PC only.");
        if (_helm.OverCap() is { } cap) return new AssistantReply("", [], false, cap);

        var picked = Model!;
        string? note = null;
        var model = picked;
        if (!_helm.HasKeyFor(picked.Provider) || !AssistantPhotos.Reads(picked.Provider, picked.Model))
        {
            if (Sonnet() is not { } sonnet)
                return new AssistantReply("", [], false, _helm.HasKeyFor(picked.Provider)
                    ? $"{picked.Name} cannot read photos, and there is no Claude key to read it with Sonnet."
                    : "Photos are read through the API: add a Claude or Grok key (AI → Models & keys on the PC).");
            model = sonnet;
            note = $"{Short(picked.Name)} không đọc được ảnh nên em nhờ {Short(sonnet.Name)}.";
        }

        AssistantLoop.Outcome? outcome = null;
        while (outcome is null)
        {
            try
            {
                outcome = await ReadPhotoAsync(model, message, photo, confirm, ct);
            }
            catch (AssistantImageException ex)
            {
                // Turned down before it did anything: once more with Sonnet, and the answer says so.
                if (note is not null || Sonnet() is not { } sonnet || sonnet.Model == model.Model) return new AssistantReply("", [], false, ex.Message);
                note = $"{Short(model.Name)} không đọc được ảnh nên em nhờ {Short(sonnet.Name)}.";
                model = sonnet;
            }
            catch (AssistantException ex)
            {
                return new AssistantReply("", [], false, ex.Message);
            }
        }
        PhotoModel = model;
        Said(said, outcome.Text, outcome.Done.Select(d => d.Summary));
        var answer = note is null ? outcome.Text : $"{note} {outcome.Text}".Trim();
        return new AssistantReply(answer, outcome.Done, outcome.Declined, outcome.Problem);
    }

    /// <summary>
    /// The words of a turn for a PC's chat: the first gets the time, and one after photo turns (each had a chat of its
    /// own) what was said and done in them.
    /// </summary>
    private string PcText(string text, bool first)
    {
        if (_untold is not { } from) return first ? $"{_helm.Now()}\n\n{text}" : text;
        _untold = null;
        var photos = string.Join("\n", _history.Skip(from).Select(h => $"{h.Role}: {h.Text}"));
        return $"{(from == 0 ? _helm.Now() + "\n\n" : "")}Earlier in this conversation:\n{photos}\n\n{text}";
    }

    /// <summary>The photo turn as words: the conversation goes on from them, and a later turn opens a new chat with this history.</summary>
    private void Said(string said, string answer, IEnumerable<string> done)
    {
        _open = null;
        _untold ??= _history.Count;
        _history.Add(("user", said.Length > 0 ? $"(sent a photo) {said}" : "(sent a photo)"));
        _history.Add(("assistant", string.Join(" ", new[] { answer }.Concat(done.Select(d => $"[{d}]")).Where(t => t.Length > 0))));
    }

    private async Task<AssistantLoop.Outcome> ReadPhotoAsync(AssistantModelOption model, string message, AssistantImage photo,
        Func<IReadOnlyList<PendingAction>, Task<bool>> confirm, CancellationToken ct)
    {
        var (chat, opened, keyId) = _helm.Open(_tools, model);
        chat.AddUser(message, photo);
        return await AssistantLoop.RunAsync(chat, _tools, confirm, u => _helm.Count(opened, keyId, u),
            () => _helm.Unavailable() ?? _helm.OverCap(), phoneHere: true, opened.Name, ct);
    }

    /// <summary>Claude's current Sonnet when there is a Claude key: the one the user can pick, else the known id.</summary>
    private AssistantModelOption? Sonnet()
    {
        if (!_helm.HasKeyFor(AiProviders.Claude)) return null;
        var claude = _helm.Models().Where(m => m.Provider == AiProviders.Claude).ToList();
        return claude.FirstOrDefault(m => m.Model == ClaudeModels.Sonnet)
            ?? claude.FirstOrDefault(m => m.Model.Contains("sonnet", StringComparison.Ordinal))
            ?? new AssistantModelOption(AiProviders.Claude, ClaudeModels.Sonnet, "Claude Sonnet 5.5");
    }

    private static string Short(string name) => name.StartsWith("Claude ", StringComparison.Ordinal) ? name[7..] : name;
}
