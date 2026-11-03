using Helm.Core.Sync;

namespace Helm.Ai;

/// <summary>
/// One API call Helm made: who made it, with which model and key, and how many tokens it took. Synced (collection
/// <see cref="AiUsageBook.Collection"/>), so the PC shows what the phone spent: the only way to know Claude's spending,
/// whose API tells no balance.
/// </summary>
public sealed record AiUsageEntry
{
    public required string Feature { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? KeyId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }

    /// <summary>Estimated from <see cref="AiPrices"/>; null for a model whose price Helm does not know. 0 on Claude Code.</summary>
    public decimal? Usd { get; init; }

    /// <summary><see cref="AiUsageBook.ApiRoute"/> or <see cref="AiUsageBook.AgentRoute"/> (older entries: none, the API).</summary>
    public string? Route { get; init; }

    /// <summary>On Claude Code: what the same call would have cost on the API (Claude Code's own estimate).</summary>
    public decimal? SavedUsd { get; init; }

    /// <summary>On Claude Code: the device that ran it.</summary>
    public string? Device { get; init; }

    public DateTimeOffset At { get; init; }
}

/// <summary>Prices per million tokens, as published when this build was made (estimates: providers change them).</summary>
public static class AiPrices
{
    /// <param name="CacheRead">Price of input read from the prompt cache.</param>
    public sealed record Price(decimal Input, decimal Output, decimal CacheRead);

    private static readonly Dictionary<string, Price> Known = new(StringComparer.Ordinal)
    {
        ["claude-fable-5-1"] = new(10m, 50m, 1m),
        ["claude-fable-5"] = new(10m, 50m, 1m),
        ["claude-opus-5-5"] = new(4m, 20m, 0.20m),
        ["claude-opus-5"] = new(5m, 25m, 0.50m),
        ["claude-opus-4-8"] = new(5m, 25m, 0.50m),
        ["claude-opus-4-7"] = new(5m, 25m, 0.50m),
        ["claude-opus-4-6"] = new(5m, 25m, 0.50m),
        ["claude-sonnet-5-5"] = new(2m, 10m, 0.20m),
        ["claude-sonnet-5"] = new(2m, 10m, 0.20m),
        ["claude-sonnet-4-6"] = new(3m, 15m, 0.30m),
        ["claude-haiku-5-5"] = new(0.10m, 0.50m, 0.01m),
        ["claude-haiku-4-5"] = new(1m, 5m, 0.10m),
        ["grok-4.7"] = new(2m, 6m, 0.50m),
        ["grok-4.6"] = new(2m, 6m, 0.50m),
        ["grok-4.3"] = new(1.25m, 2.50m, 0.30m),
        ["grok-build-0.1"] = new(1m, 2m, 0.25m),
    };

    public static Price? For(string model) => Known.GetValueOrDefault(model);

    /// <summary>
    /// The cost of one call. Cache writes cost a quarter more than plain input on Claude. <paramref name="input"/> is
    /// the input not read from the cache.
    /// </summary>
    public static decimal? Usd(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        if (For(model) is not { } p) return null;
        return (input * p.Input + cacheWrite * p.Input * 1.25m + cacheRead * p.CacheRead + output * p.Output) / 1_000_000m;
    }
}

/// <summary>What Helm's API calls cost, per feature and per day or month (the user's local days).</summary>
public sealed class AiUsageBook(ISyncedLog<AiUsageEntry> log, TimeProvider? time = null)
{
    public const string Collection = "ai.usage";
    public const string ApiRoute = "api";
    /// <summary>An agent on a Helm PC ran it (Codex, Claude Code, Grok…: <see cref="AiUsageEntry.Provider"/> names it).</summary>
    public const string AgentRoute = "agent";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AiUsageEntry Record(string feature, AiProvider provider, string model, string? keyId, long input, long output, long cacheRead, long cacheWrite)
    {
        var entry = new AiUsageEntry
        {
            Feature = feature,
            Provider = provider.Id,
            Model = model,
            KeyId = keyId,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            Usd = AiPrices.Usd(model, input, output, cacheRead, cacheWrite),
            Route = ApiRoute,
            At = _time.GetUtcNow(),
        };
        log.Append(entry);
        return entry;
    }

    /// <summary>A call an agent ran on a Helm PC: free for the user, worth <paramref name="savedUsd"/> on the API.</summary>
    public AiUsageEntry RecordAgent(string feature, string agent, string model, string device, decimal? savedUsd)
    {
        var entry = new AiUsageEntry
        {
            Feature = feature,
            Provider = agent,
            Model = model,
            Usd = 0,
            Route = AgentRoute,
            SavedUsd = savedUsd,
            Device = device,
            At = _time.GetUtcNow(),
        };
        log.Append(entry);
        return entry;
    }

    /// <summary>What Claude Code on Helm PCs saved this month: the API price of the calls it ran instead.</summary>
    public decimal SavedThisMonth()
    {
        var now = Local(_time.GetUtcNow());
        return Entries(null).Where(e => e.Route == AgentRoute && Local(e.At) is var at && at.Year == now.Year && at.Month == now.Month)
            .Sum(e => e.SavedUsd ?? 0);
    }

    public decimal SpentToday(string? feature = null)
    {
        var today = Local(_time.GetUtcNow()).Date;
        return Entries(feature).Where(e => Local(e.At).Date == today).Sum(e => e.Usd ?? 0);
    }

    public decimal SpentThisMonth(string? feature = null)
    {
        var now = Local(_time.GetUtcNow());
        return Entries(feature).Where(e => Local(e.At) is var at && at.Year == now.Year && at.Month == now.Month).Sum(e => e.Usd ?? 0);
    }

    /// <summary>Today's calls of every feature: how many an agent ran, how many the API, and what the API ones cost.</summary>
    public (int Agent, int Api, decimal ApiUsd) Today()
    {
        var today = Local(_time.GetUtcNow()).Date;
        var calls = Entries(null).Where(e => Local(e.At).Date == today).ToList();
        var agent = calls.Count(e => e.Route == AgentRoute);
        return (agent, calls.Count - agent, calls.Where(e => e.Route != AgentRoute).Sum(e => e.Usd ?? 0));
    }

    /// <summary>This month's calls per feature: how many, and their estimated cost.</summary>
    public IReadOnlyList<(string Feature, int Calls, decimal Usd)> ThisMonth()
    {
        var now = Local(_time.GetUtcNow());
        return [.. Entries(null).Where(e => Local(e.At) is var at && at.Year == now.Year && at.Month == now.Month)
            .GroupBy(e => e.Feature, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count(), g.Sum(e => e.Usd ?? 0)))
            .OrderByDescending(r => r.Item3)];
    }

    private IEnumerable<AiUsageEntry> Entries(string? feature) =>
        log.All().Select(i => i.Value).Where(e => feature is null || e.Feature == feature);

    private static DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local);
}
