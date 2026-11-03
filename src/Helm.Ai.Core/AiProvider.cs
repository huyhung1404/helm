namespace Helm.Ai;

/// <summary>A company whose AI models Helm can call with the user's own API key.</summary>
/// <param name="Id">Stable id stored with each key ("anthropic", "xai").</param>
/// <param name="KeyPrefix">Every key of this provider starts with it; catches a key pasted in the wrong place.</param>
/// <param name="ConsoleUrl">Where the user creates keys and buys credit.</param>
public sealed record AiProvider(string Id, string Name, string Company, string KeyPrefix, string ConsoleUrl);

public static class AiProviders
{
    public static AiProvider Claude { get; } = new("anthropic", "Claude", "Anthropic", "sk-ant-", "https://platform.claude.com/settings/keys");

    public static AiProvider Grok { get; } = new("xai", "Grok", "xAI", "xai-", "https://console.x.ai");

    /// <summary>Every provider Helm knows, in the order the AI page lists them.</summary>
    public static IReadOnlyList<AiProvider> All { get; } = [Claude, Grok];

    public static AiProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
