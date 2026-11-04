using Helm.Core.Settings;

namespace Helm.Ai;

/// <summary>The AI features of Helm, each with its own choice of model (<see cref="AiFeatureSettings"/>).</summary>
public static class AiFeatureIds
{
    /// <summary>The phone's voice assistant: the Quick Settings tile and its card.</summary>
    public const string AskHelm = "ask-helm";

    /// <summary>Wallet: a new transaction the history cannot file (runs by itself: a PC agent only, unless chosen otherwise).</summary>
    public const string WalletCategorize = "wallet-categorize";

    /// <summary>Wallet: reads a bank notification Helm's parser missed, when the user asks it to.</summary>
    public const string WalletReadNotification = "wallet-read-notification";

    /// <summary>Translate, fix, rewrite, summarize or answer a selected text (Android's text selection → Ask Helm's card).</summary>
    public const string TextActions = "text-actions";

    /// <summary>A word added on the phone, analysed at once instead of waiting for the PC (Vocabulary).</summary>
    public const string VocabQuick = "vocab-quick";

    /// <summary>A held sentence in Novel Reader: meaning, grammar, a natural translation, words to learn.</summary>
    public const string SentenceExplain = "sentence-explain";

    /// <summary>Tracker: steps proposed for a big task, tasks proposed from a note.</summary>
    public const string TaskProposals = "task-proposals";

    /// <summary>A8/M4: Ask Helm searches the web (weather, rates, gold, news, checking a post). Off until the user turns it on.</summary>
    public const string WebSearch = "web-search";

    // Jobs a Helm PC runs by itself on its agents (Helm.Ai.Auto.AutoJobCatalog): each brief reaches the phone.

    /// <summary>P3: the morning brief (today's tasks, habits to do, yesterday's spending).</summary>
    public const string MorningBrief = "morning-brief";

    /// <summary>P4: the evening review (done, slipping to tomorrow, one suggestion).</summary>
    public const string EveningReview = "evening-review";

    /// <summary>P5: habit coaching from the reasons given for skipped days.</summary>
    public const string HabitCoach = "habit-coach";

    /// <summary>W3: the week's and the month's spending.</summary>
    public const string SpendingDigest = "spending-digest";

    /// <summary>D2: birthdays, giỗ and renewals, a few days before.</summary>
    public const string ImportantDays = "important-days";

    /// <summary>O2: a title and tags for new notes without a title.</summary>
    public const string NoteTitles = "note-titles";

    /// <summary>O5: what to keep or drop in Scratch (suggestions only).</summary>
    public const string ScratchTidy = "scratch-tidy";
}

/// <summary>Where a feature's calls may run: Claude Code on a Helm PC (the user's plan, no cost per call) or the API.</summary>
public enum AiRoutePolicy
{
    /// <summary>Claude Code on a Helm PC when one answers within <see cref="AiFeatureChoice.PcWaitSeconds"/>, else the API.</summary>
    PreferPc,

    /// <summary>Only Claude Code on a Helm PC: the call waits for one.</summary>
    PcOnly,

    /// <summary>Only the API.</summary>
    ApiOnly,
}

/// <summary>How one feature calls AI: on or off, which provider and model, how hard it thinks, where it runs, its daily cap.</summary>
public sealed class AiFeatureChoice
{
    public bool Enabled { get; set; } = true;

    public AiRoutePolicy Route { get; set; } = AiRoutePolicy.PreferPc;

    /// <summary>How long a call waits for a Helm PC's Claude Code before it goes to the API (<see cref="AiRoutePolicy.PreferPc"/>).</summary>
    public int PcWaitSeconds { get; set; } = 8;

    /// <summary><see cref="AiProvider.Id"/>.</summary>
    public string Provider { get; set; } = AiProviders.Claude.Id;

    public string Model { get; set; } = ClaudeModels.Haiku;

    /// <summary>
    /// The agent on a Helm PC the user picked by name ("Grok", "Claude Code"), running <see cref="Model"/>; only with
    /// <see cref="AiRoutePolicy.PcOnly"/>, where no other agent and never the API take the turn. Null: any agent, in the
    /// PC's order, each on its own model when <see cref="Model"/> is not one of its.
    /// </summary>
    public string? Agent { get; set; }

    /// <summary>One of <see cref="AiEfforts.Levels"/> ("low" … "max"); a model that takes fewer gets the nearest it takes.</summary>
    public string Effort { get; set; } = "low";

    /// <summary>The feature stops for the day once the API calls it made today cost this much (USD, estimated).</summary>
    public decimal DailyCapUsd { get; set; } = 0.5m;

    /// <summary>A job that runs by itself: its local time of day, "07:00" (null: the job's own default).</summary>
    public string? At { get; set; }

    public AiFeatureChoice Copy() => (AiFeatureChoice)MemberwiseClone();
}

/// <summary>Every feature's choice, synced (collection settings.ai-features): the phone picks what the PC set and back.</summary>
public sealed class AiFeatureSettings : IVersionedSettings
{
    public const string StoreId = "ai-features";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public Dictionary<string, AiFeatureChoice> Features { get; set; } = [];

    /// <summary>The choice for <paramref name="feature"/>, or its default when none was made yet.</summary>
    public AiFeatureChoice For(string feature) => Features.TryGetValue(feature, out var choice) ? choice : Auto.AutoJobCatalog.DefaultChoice(feature);
}

public static class ClaudeModels
{
    /// <summary>The cheapest and fastest current model: Ask Helm's default.</summary>
    public const string Haiku = "claude-haiku-5-5";

    /// <summary>The current Sonnet: what reads a photo when the model picked cannot.</summary>
    public const string Sonnet = "claude-sonnet-5-5";
}
