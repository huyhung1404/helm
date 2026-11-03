namespace Helm.Modules.SlackBot;

/// <summary>
/// The Claude models and effort levels Slack Bot offers, as Claude Code in VS Code does. Models are Claude Code's
/// aliases, so each always means the latest of its kind.
/// </summary>
public static class SlackAiOptions
{
    public static IReadOnlyList<SlackBotChoice<string>> Models { get; } =
    [
        new("", "Claude Code's default"),
        new("opus", "Opus · complex work and everyday tasks"),
        new("fable", "Fable · the toughest challenges"),
        new("sonnet", "Sonnet · efficient for simpler tasks"),
        new("haiku", "Haiku · fastest, light on your plan"),
    ];

    public static IReadOnlyList<SlackBotChoice<string>> Efforts { get; } =
    [
        new("", "Claude Code's default"),
        new("low", "Low"),
        new("medium", "Medium"),
        new("high", "High"),
        new("xhigh", "Extra high"),
        new("max", "Max"),
    ];

    /// <summary>Short words for a model or an effort ("" = the default named by <paramref name="none"/>).</summary>
    public static string Short(string value, string none) => value switch
    {
        "" => none,
        "xhigh" => "Extra high",
        _ => char.ToUpperInvariant(value[0]) + value[1..],
    };
}
