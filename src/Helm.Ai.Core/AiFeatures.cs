using Helm.Core.Settings;

namespace Helm.Ai;

/// <summary>The AI features of Helm, each with its own choice of model (<see cref="AiFeatureSettings"/>).</summary>
public static class AiFeatureIds
{
    /// <summary>The phone's voice assistant: the Quick Settings tile and its card.</summary>
    public const string AskHelm = "ask-helm";

    /// <summary>Translate, fix, rewrite, summarize or answer a selected text (Android's text selection → Ask Helm's card).</summary>
    public const string TextActions = "text-actions";

    /// <summary>A word added on the phone, analysed at once instead of waiting for the PC (Vocabulary).</summary>
    public const string VocabQuick = "vocab-quick";

    /// <summary>A held sentence in Novel Reader: meaning, grammar, a natural translation, words to learn.</summary>
    public const string SentenceExplain = "sentence-explain";

    /// <summary>Tracker: steps proposed for a big task, tasks proposed from a note.</summary>
    public const string TaskProposals = "task-proposals";
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

    /// <summary>"low", "medium" or "high" (models that take an effort).</summary>
    public string Effort { get; set; } = "low";

    /// <summary>The feature stops for the day once the API calls it made today cost this much (USD, estimated).</summary>
    public decimal DailyCapUsd { get; set; } = 0.5m;

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
    public AiFeatureChoice For(string feature) => Features.TryGetValue(feature, out var choice) ? choice : new AiFeatureChoice();
}

public static class ClaudeModels
{
    /// <summary>The cheapest and fastest current model: Ask Helm's default.</summary>
    public const string Haiku = "claude-haiku-5-5";
}
