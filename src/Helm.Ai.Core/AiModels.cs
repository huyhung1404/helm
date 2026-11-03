namespace Helm.Ai;

/// <summary>Which of the models a key's test found can be picked for a feature: chat models only.</summary>
public static class AiModels
{
    /// <summary>
    /// Parts of an id ("grok-2-image-1212", "grok-imagine-video", "text-embedding-3") that mark a model which draws,
    /// films, embeds, speaks or listens instead of chatting. Matched at the start of each dash-separated part.
    /// </summary>
    private static readonly string[] NotChat = ["imag", "video", "embed", "tts", "speech", "audio", "whisper", "transcri", "moderat", "rerank"];

    /// <summary>The model a provider's features fall back on when the one picked is no longer offered.</summary>
    public static string DefaultFor(AiProvider provider) => provider == AiProviders.Grok ? GrokModels.Default : ClaudeModels.Haiku;

    /// <summary>A model as a status line says it: "claude-sonnet-5-5" → "Sonnet 5.5", "grok-4.7" → "Grok 4.7".</summary>
    public static string ShortName(string id)
    {
        var parts = id.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return id;
        if (parts.Count > 1 && parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        // A dated snapshot ("…-20250805") reads the same as its model.
        if (parts.Count > 1 && parts[^1].Length == 8 && parts[^1].All(char.IsAsciiDigit)) parts.RemoveAt(parts.Count - 1);
        var name = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        return parts.Count == 1 ? name : $"{name} {string.Join('.', parts.Skip(1))}";
    }

    public static bool IsChat(string id) =>
        !id.ToLowerInvariant().Split('-', '_', '.', ':').Any(part => NotChat.Any(p => part.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>The chat models of a key's last test; empty when the key was never tested or the test found none.</summary>
    public static IReadOnlyList<AiModel> Chat(AiKeyCheck? check) => check is null ? [] : [.. check.Models.Where(m => IsChat(m.Id))];

    /// <summary>What a provider offers when no test of its key listed any model.</summary>
    public static IReadOnlyList<AiModel> Fallback(AiProvider provider) => provider == AiProviders.Grok
        ? [new AiModel(GrokModels.Default, GrokModels.Default)]
        : [new AiModel(ClaudeModels.Haiku, "Claude Haiku 5.5"), new AiModel("claude-sonnet-5-5", "Claude Sonnet 5.5")];
}

public static class GrokModels
{
    /// <summary>Grok's model for a feature that has not picked one, and when the one picked is gone.</summary>
    public const string Default = "grok-4.7";
}
