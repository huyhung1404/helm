using Microsoft.Extensions.DependencyInjection;

namespace Helm.Core.Sync;

/// <summary>
/// The collections of Helm's own AI (keys, usage, agent jobs, prompts, briefs, the features' choices), removed in 0.95
/// and 0.96, whose records are still on the users' sync servers. Nothing reads or writes them any more, and nothing
/// deletes them: deleting synced records is a separate step the user decides, one collection at a time. They stay
/// known to the engine with the policies they had, so sync treats their records exactly as before: never as unknown
/// collections (which would stop the daily blob clean-up on every device), and a bulk deletion of the keys still held
/// for the user.
/// </summary>
public static class LeftoverAiCollections
{
    /// <summary>The AI group's prefix, still listed under General → Sync and accepted by the connector's "sync".</summary>
    public const string Prefix = "ai.";

    /// <summary>The features' choices of model (Helm's old AI settings), synced outside the group's prefix.</summary>
    public const string FeatureSettings = "settings.ai-features";

    /// <summary>The API keys and the Grok management keys: their deletions were guarded.</summary>
    public static readonly IReadOnlyList<string> Keys = ["ai.keys", "ai.management"];

    /// <summary>What the calls cost: an append-only log.</summary>
    public const string Usage = "ai.usage";

    /// <summary>The phone's jobs for a PC's agents and the PCs that took them, the Skill Hub's prompts, the briefs and the automatic jobs' runs.</summary>
    public static readonly IReadOnlyList<string> Others = ["ai.jobs", "ai.workers", "ai.prompts", "ai.briefs", "ai.autoruns"];

    /// <summary>Every leftover collection.</summary>
    public static IReadOnlyList<string> All => [.. Keys, Usage, .. Others, FeatureSettings];

    public static IServiceCollection AddLeftoverAiCollections(this IServiceCollection services)
    {
        foreach (var name in Keys)
            services.AddSingleton(new SyncCollectionDescriptor(name, 1, SyncConflictPolicy.LastWriterWins, changeGuard: new SyncChangeGuard()));
        services.AddSingleton(new SyncCollectionDescriptor(Usage, 1, SyncConflictPolicy.RemoteWins));
        foreach (var name in Others)
            services.AddSingleton(new SyncCollectionDescriptor(name, 1, SyncConflictPolicy.LastWriterWins));
        services.AddSingleton(new SyncCollectionDescriptor(FeatureSettings, 1, SyncConflictPolicy.LastWriterWins));
        services.AddSyncGroup(Prefix, "AI keys and usage (no longer used)");
        return services;
    }
}
