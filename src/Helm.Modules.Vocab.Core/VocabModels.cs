using System.Text.Json.Serialization;

namespace Helm.Modules.Vocab;

public enum VocabLanguage
{
    English,
    Chinese,
}

/// <summary>Where a word stands: waiting for an AI agent to break it into parts, or broken down.</summary>
public enum VocabStatus
{
    Pending,
    Analyzed,
}

/// <summary>What a part is. Strokes are not parts: they carry no meaning.</summary>
public enum VocabPartKind
{
    /// <summary>English: re-, un-, trans-.</summary>
    Prefix,
    /// <summary>English: a Latin or Greek root (lat, spect, graph).</summary>
    Root,
    /// <summary>English: -ive, -tion, -ly.</summary>
    Suffix,
    /// <summary>English: a whole word inside a longer one (relate in relative).</summary>
    Word,
    /// <summary>Chinese: one character of a word (补 in 补救).</summary>
    Character,
    /// <summary>Chinese: a component or radical of a character (求, 攵 in 救).</summary>
    Component,
}

/// <summary>What a Chinese component does in the character above it (English parts are <see cref="Plain"/>).</summary>
public enum VocabPartRole
{
    Plain,
    /// <summary>Gives the character its meaning (the semantic part of a 形声 character).</summary>
    Meaning,
    /// <summary>Gives the character its sound (the phonetic part).</summary>
    Sound,
}

/// <summary>A part used by a word or by a bigger part, with the role it plays there.</summary>
public sealed record VocabPartLink
{
    public string PartId { get; init; } = "";

    public VocabPartRole Role { get; init; }
}

/// <summary>A word the user added (synced record in <c>vocab.words</c>).</summary>
public sealed record VocabWord
{
    public VocabLanguage Language { get; init; }

    /// <summary>As the user typed it: "relative", "补救".</summary>
    public string Text { get; init; } = "";

    /// <summary>The user's own note: where they met the word, a sentence. Optional.</summary>
    public string Note { get; init; } = "";

    public VocabStatus Status { get; init; }

    /// <summary>English: IPA (/ˈrel.ə.tɪv/). Chinese: pinyin with tone marks (bǔjiù).</summary>
    public string Phonetic { get; init; } = "";

    /// <summary>In Vietnamese.</summary>
    public string Meaning { get; init; } = "";

    /// <summary>Chinese: the Hán Việt reading (bổ cứu).</summary>
    public string HanViet { get; init; } = "";

    /// <summary>Chinese: the traditional form when it differs (補救); empty otherwise.</summary>
    public string Traditional { get; init; } = "";

    /// <summary>In Vietnamese, one sentence: how the parts' meanings make the word's.</summary>
    public string Explanation { get; init; } = "";

    public IReadOnlyList<VocabPartLink> Parts { get; init; } = [];

    /// <summary>Sentences that use the word, each with a Vietnamese translation (the fill-the-gap card hides the word in them).</summary>
    public IReadOnlyList<VocabExample> Examples { get; init; } = [];

    /// <summary>Words it usually goes with: "make a decision", "补救措施".</summary>
    public IReadOnlyList<string> Collocations { get; init; } = [];

    /// <summary>In Vietnamese: a short story that ties the parts to the meaning, to remember it by.</summary>
    public string Mnemonic { get; init; } = "";

    /// <summary>Words easy to mix it up with (affect / effect; 己 / 已), each with what tells them apart.</summary>
    public IReadOnlyList<VocabConfusable> Confusables { get; init; } = [];

    /// <summary>CEFR level for English (A1 … C2), HSK level for Chinese (HSK1 … HSK9); empty when unknown.</summary>
    public string Level { get; init; } = "";

    /// <summary>In Vietnamese: "danh từ", "động từ"…</summary>
    public string PartOfSpeech { get; init; } = "";

    /// <summary>In Vietnamese, one or two words: "gia đình", "công việc"…</summary>
    public string Topic { get; init; } = "";

    /// <summary>The user's own labels: "Aptis", "Truyện"…</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>What the user said was wrong when asking for the word to be analyzed again.</summary>
    public string RedoNote { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? AnalyzedAt { get; init; }

    [JsonIgnore]
    public bool IsAnalyzed => Status == VocabStatus.Analyzed;
}

/// <summary>
/// A meaningful piece of words (synced record in <c>vocab.parts</c>), shared by every word that has it, so "lat" means
/// the same in relative and translate. Two pieces written alike but meaning different things are two parts.
/// </summary>
public sealed record VocabPart
{
    public VocabLanguage Language { get; init; }

    /// <summary>As written: "re-", "lat", "-ive", "救", "攵".</summary>
    public string Form { get; init; } = "";

    public VocabPartKind Kind { get; init; }

    public string Phonetic { get; init; } = "";

    /// <summary>In Vietnamese.</summary>
    public string Meaning { get; init; } = "";

    public string HanViet { get; init; } = "";

    public string Traditional { get; init; } = "";

    /// <summary>In Vietnamese: where it comes from (Latin latus, from ferre "mang"), other forms of it.</summary>
    public string Origin { get; init; } = "";

    /// <summary>Its own smaller parts; empty at the bottom of the tree.</summary>
    public IReadOnlyList<VocabPartLink> Parts { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A sentence with the word in it, and its Vietnamese translation.</summary>
public sealed record VocabExample
{
    public string Sentence { get; init; } = "";

    public string Translation { get; init; } = "";
}

/// <summary>A word easy to mix up with another, and what tells them apart (in Vietnamese).</summary>
public sealed record VocabConfusable
{
    public string Word { get; init; } = "";

    public string Note { get; init; } = "";
}

/// <summary>One answer to the quiz (append-only log <c>vocab.reviews</c>): did the user remember the word.</summary>
public sealed record VocabReview
{
    public string WordId { get; init; } = "";

    public DateTimeOffset At { get; init; }

    public bool Remembered { get; init; }
}
