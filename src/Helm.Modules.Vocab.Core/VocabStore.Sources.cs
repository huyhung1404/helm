using Helm.Core.Sync;

namespace Helm.Modules.Vocab;

// Pasted texts (articles, song lyrics): an agent picks the words worth learning, the user keeps or drops each, and the
// kept ones join the waiting words with the line they come from.
public sealed partial class VocabStore
{
    private readonly ISyncedCollection<VocabSource>? _sources;

    private ISyncedCollection<VocabSource> Sources =>
        _sources ?? throw new InvalidOperationException("This store was built without a collection for pasted texts.");

    /// <summary>Every text, newest first.</summary>
    public IReadOnlyList<SyncedItem<VocabSource>> AllSources() =>
        _sources is null ? [] : _sources.All().OrderByDescending(s => s.Value.CreatedAt).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();

    public VocabSource? GetSource(string id) => _sources?.Get(id);

    /// <summary>Texts waiting for an agent to pick their words, oldest first, that no agent has taken.</summary>
    public IReadOnlyList<SyncedItem<VocabSource>> WaitingSources() =>
        AllSources().Where(s => s.Value.Status == VocabSourceStatus.Waiting).Reverse().ToList();

    /// <summary>Texts waiting that no MCP agent has taken: the ones Helm's own agent may read.</summary>
    public IReadOnlyList<SyncedItem<VocabSource>> WaitingSourcesUnclaimed() =>
        WaitingSources().Where(s => !(s.Value.ClaimedUntil is { } until && until > Now)).ToList();

    /// <summary>An agent is reading a text right now.</summary>
    public bool HasClaimedSource() =>
        WaitingSources().Any(s => s.Value.ClaimedUntil is { } until && until > Now);

    /// <summary>Saves a text to learn words from.</summary>
    /// <exception cref="ArgumentException">Nothing was pasted, or too much.</exception>
    public string AddSource(string title, string body)
    {
        body = body.Trim();
        if (body.Length == 0) throw new ArgumentException("Paste a text first.", nameof(body));
        if (body.Length > VocabSource.MaxBodyLength) throw new ArgumentException($"A text is at most {VocabSource.MaxBodyLength} characters.", nameof(body));
        lock (_gate)
        {
            return Sources.Add(new VocabSource { Title = title.Trim(), Body = body, Status = VocabSourceStatus.Waiting, CreatedAt = Now });
        }
    }

    /// <summary>Marks texts as taken by an MCP agent for <see cref="ClaimFor"/>.</summary>
    public void ClaimSources(IEnumerable<string> ids)
    {
        var until = Now + ClaimFor;
        foreach (var id in ids)
            UpdateSource(id, s => s.Status == VocabSourceStatus.Waiting ? s with { ClaimedUntil = until } : s);
    }

    /// <returns>False when the text no longer exists.</returns>
    public bool UpdateSource(string id, Func<VocabSource, VocabSource> change)
    {
        lock (_gate)
        {
            if (Sources.Get(id) is not { } current) return false;
            var updated = change(current);
            if (updated != current) Sources.Upsert(id, updated);
            return true;
        }
    }

    public bool DeleteSource(string id)
    {
        lock (_gate) return Sources.Delete(id);
    }

    /// <summary>
    /// Saves the words an agent picked from a text. A word the user already has is kept in the list but not ticked; a word
    /// listed twice counts once. The text then waits for the user's choice.
    /// </summary>
    /// <returns>The proposals as saved.</returns>
    public IReadOnlyList<VocabProposal> Propose(string sourceId, IEnumerable<VocabProposal> picked)
    {
        var seen = new HashSet<string>();
        var proposals = new List<VocabProposal>();
        foreach (var p in picked)
        {
            var word = VocabText.Tidy(p.Word);
            if (word.Length == 0 || VocabText.Length(word) > VocabText.MaxWordLength || !seen.Add(VocabText.Key(word))) continue;
            var existing = FindWord(word);
            proposals.Add(p with { Word = word, Chosen = existing is null && p.Chosen, ExistingWordId = existing?.Id, AddedWordId = null });
            if (proposals.Count == VocabSource.MaxProposals) break;
        }
        var saved = UpdateSource(sourceId, s => s with
        {
            Status = proposals.Count > 0 ? VocabSourceStatus.Proposed : VocabSourceStatus.Done,
            Proposals = proposals,
            ProposedAt = Now,
            ClaimedUntil = null,
        });
        return saved ? proposals : throw new ArgumentException("The user has no such text.", nameof(sourceId));
    }

    /// <summary>Ticks or unticks a proposed word.</summary>
    public bool SetChosen(string sourceId, string word, bool chosen)
    {
        var key = VocabText.Key(word);
        return UpdateSource(sourceId, s => s with
        {
            Proposals = s.Proposals.Select(p => VocabText.Key(p.Word) == key && p.AddedWordId is null && p.ExistingWordId is null ? p with { Chosen = chosen } : p).ToList(),
        });
    }

    /// <summary>Adds the ticked words to the waiting list, each with the line it comes from; the text is done after that (words left unticked are dropped).</summary>
    /// <returns>How many words were added.</returns>
    public int AddChosen(string sourceId)
    {
        lock (_gate)
        {
            if (Sources.Get(sourceId) is not { } source) return 0;
            var proposals = source.Proposals.ToList();
            var added = 0;
            for (var i = 0; i < proposals.Count; i++)
            {
                var p = proposals[i];
                if (!p.Chosen || p.AddedWordId is not null) continue;
                var (id, isNew) = AddWord(p.Word, source.Title);
                UpdateWord(id, w => w with { SourceId = w.SourceId ?? sourceId, SourceSentence = w.SourceSentence.Length > 0 ? w.SourceSentence : p.Sentence });
                proposals[i] = p with { AddedWordId = id, ExistingWordId = isNew ? null : id, Chosen = false };
                if (isNew) added++;
            }
            UpdateSource(sourceId, s => s with { Proposals = proposals, Status = VocabSourceStatus.Done });
            return added;
        }
    }
}
