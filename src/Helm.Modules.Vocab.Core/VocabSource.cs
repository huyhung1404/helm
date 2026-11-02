namespace Helm.Modules.Vocab;

/// <summary>Where a pasted text stands: waiting for an agent to pick its words, words proposed, or all added.</summary>
public enum VocabSourceStatus
{
    Waiting,
    Proposed,
    Done,
}

/// <summary>A word an agent picked from a text, for the user to keep or drop before it is added.</summary>
public sealed record VocabProposal
{
    /// <summary>As it should be added (Chinese in simplified characters).</summary>
    public string Word { get; init; } = "";

    /// <summary>Pinyin or IPA, optional: only to help the user decide.</summary>
    public string Phonetic { get; init; } = "";

    /// <summary>In Vietnamese, short.</summary>
    public string Meaning { get; init; } = "";

    /// <summary>The sentence or line of the text the word comes from (as written there).</summary>
    public string Sentence { get; init; } = "";

    /// <summary>In Vietnamese: why it is worth learning.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Kept by the user (ticked); false for a word the user already has.</summary>
    public bool Chosen { get; init; } = true;

    /// <summary>The user's word with this spelling, when they already have it.</summary>
    public string? ExistingWordId { get; init; }

    /// <summary>The word added from this proposal.</summary>
    public string? AddedWordId { get; init; }
}

/// <summary>A text the user pasted (an article, song lyrics) to learn words from (synced record in <c>vocab.sources</c>).</summary>
public sealed record VocabSource
{
    public const int MaxBodyLength = 20000;
    public const int MaxProposals = 80;

    /// <summary>Where it comes from: a song and singer, a chapter, a page. Optional.</summary>
    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    public VocabSourceStatus Status { get; init; }

    public IReadOnlyList<VocabProposal> Proposals { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ProposedAt { get; init; }

    /// <summary>An agent took this text to read it: Helm's own agent leaves it alone until this time passes.</summary>
    public DateTimeOffset? ClaimedUntil { get; init; }
}
