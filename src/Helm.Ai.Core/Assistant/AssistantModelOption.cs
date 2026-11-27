namespace Helm.Ai.Assistant;

/// <summary>A model the user can pick for a feature: a provider they have a key for, and one of its chat models.</summary>
public sealed record AssistantModelOption(AiProvider Provider, string Model, string Name)
{
    /// <summary>Every model the user can pick: the chat models of each provider they have a tested key for.</summary>
    public static IReadOnlyList<AssistantModelOption> Offered(AiKeyStore keys, IEnumerable<IAssistantChatFactory> factories)
    {
        var made = factories.ToList();
        var options = new List<AssistantModelOption>();
        foreach (var provider in AiProviders.All)
        {
            if (keys.DefaultFor(provider) is not { } key || !made.Any(f => f.Provider == provider)) continue;
            var models = AiModels.Chat(key.Value.LastCheck);
            if (models.Count == 0) models = AiModels.Fallback(provider);
            options.AddRange(models.Select(m => new AssistantModelOption(provider, m.Id, m.Name)));
        }
        return options;
    }
}
