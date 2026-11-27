using System.Text.Json;

namespace Helm.Connector;

/// <summary>
/// The tools whose data the connector syncs (its allowlist, <see cref="Helm.Core.Sync.SyncEngine.SetIncluded"/>):
/// <c>--sync wallet,tracker</c>, else <c>"sync"</c> in &lt;data&gt;/connector.json, else <see cref="Default"/>. Only these
/// tools' stores and agent tools are registered; nothing else is downloaded or uploaded. "ai" (Helm's own AI, gone since
/// 0.96) is still accepted, so a connector.json that lists it keeps working: it syncs the old AI records as before
/// (<see cref="Helm.Core.Sync.LeftoverAiCollections"/>) and registers no tool.
/// </summary>
internal sealed record SyncSelection(IReadOnlyList<string> Groups)
{
    public const string FileName = "connector.json";

    public static readonly IReadOnlyList<string> Default = ["wallet.", "tracker.", "habits."];

    /// <summary>The groups the connector can host, with the names the apps give them. Vault is never one of them.</summary>
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>
    {
        [Helm.Core.Sync.LeftoverAiCollections.Prefix] = "AI (old records only)",
        ["wallet."] = "Wallet",
        ["tracker."] = "Tracker",
        ["habits."] = "Habits",
        ["notes."] = "Notes",
        ["missions."] = "Missions",
        ["vocab."] = "Vocabulary",
        ["novel."] = "Novel Reader",
    };

    /// <summary>What the engine syncs: the groups, and with "ai" the old AI settings (the features' choices), outside its prefix.</summary>
    public IReadOnlyList<string> Prefixes =>
        [.. Groups, .. Has(Helm.Core.Sync.LeftoverAiCollections.Prefix) ? new[] { Helm.Core.Sync.LeftoverAiCollections.FeatureSettings } : []];

    public bool Has(string group) => Groups.Contains(group);

    /// <exception cref="FormatException">An unknown group, or an unreadable connector.json.</exception>
    public static SyncSelection Resolve(string? option, string dataDirectory)
    {
        if (option is not null) return Parse(option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var file = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(file)) return new SyncSelection(Default);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            if (!json.RootElement.TryGetProperty("sync", out var list) || list.ValueKind != JsonValueKind.Array) return new SyncSelection(Default);
            return Parse(list.EnumerateArray().Select(e => e.GetString() ?? ""));
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{file} is not valid JSON: {ex.Message}", ex);
        }
    }

    public static SyncSelection Parse(IEnumerable<string> names)
    {
        var groups = new List<string>();
        foreach (var name in names)
        {
            var group = name.Trim().ToLowerInvariant().TrimEnd('.') + ".";
            if (!Known.ContainsKey(group))
                throw new FormatException($"'{name}' is not something the connector syncs. Pick from: {string.Join(", ", Known.Keys.Select(k => k.TrimEnd('.')))}.");
            if (!groups.Contains(group)) groups.Add(group);
        }
        if (groups.Count == 0) throw new FormatException("The connector needs at least one thing to sync.");
        return new SyncSelection(groups);
    }
}
