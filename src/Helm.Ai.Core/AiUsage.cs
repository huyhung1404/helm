using Helm.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>Web searches the provider ran for the call (Claude's server-side search), each <see cref="AiPrices.WebSearchUsd"/>.</summary>
    public long WebSearches { get; init; }

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

    /// <summary>The model names of each provider start with this (Claude: "claude-", Grok: "grok-").</summary>
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.Ordinal)
    {
        [AiProviders.Claude.Id] = "claude-",
        [AiProviders.Grok.Id] = "grok-",
    };

    /// <summary>
    /// The dearest price Helm knows for <paramref name="provider"/>'s models (any provider's when it knows none): what a
    /// model with no known price is counted at against a daily cap, so a new model never spends past it unseen.
    /// </summary>
    public static Price Ceiling(string provider)
    {
        var own = Prefixes.TryGetValue(provider, out var prefix) ? Known.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k.Value).ToList() : [];
        return (own.Count > 0 ? own : [.. Known.Values]).MaxBy(p => p.Input + p.Output)!;
    }

    private static decimal Cost(Price p, long input, long output, long cacheRead, long cacheWrite) =>
        (input * p.Input + cacheWrite * p.Input * 1.25m + cacheRead * p.CacheRead + output * p.Output) / 1_000_000m;

    /// <summary>One web search on Claude's API: $10 per 1,000, on top of the tokens its results add (a failed one is free).</summary>
    public const decimal WebSearchUsd = 0.01m;

    /// <summary>
    /// The cost of one call. Cache writes cost a quarter more than plain input on Claude. <paramref name="input"/> is
    /// the input not read from the cache.
    /// </summary>
    public static decimal? Usd(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        return For(model) is { } p ? Cost(p, input, output, cacheRead, cacheWrite) : null;
    }

    /// <summary>An API call's cost for a daily cap: its estimate, else the provider's <see cref="Ceiling"/> (never 0).</summary>
    public static decimal ForCap(AiUsageEntry entry) =>
        entry.Usd ?? Cost(Ceiling(entry.Provider), entry.InputTokens, entry.OutputTokens, entry.CacheReadTokens, entry.CacheWriteTokens);
}

/// <summary>What Helm's API calls cost, per feature and per day or month (the user's local days).</summary>
public sealed class AiUsageBook(ISyncedLog<AiUsageEntry> log, TimeProvider? time = null, ILogger? logger = null)
{
    public const string Collection = "ai.usage";
    public const string ApiRoute = "api";
    /// <summary>An agent on a Helm PC ran it (Codex, Claude Code, Grok…: <see cref="AiUsageEntry.Provider"/> names it).</summary>
    public const string AgentRoute = "agent";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly HashSet<string> _unpriced = new(StringComparer.Ordinal);

    /// <param name="webSearches">Web searches the call ran: their fee is added to <see cref="AiUsageEntry.Usd"/>.</param>
    public AiUsageEntry Record(string feature, AiProvider provider, string model, string? keyId, long input, long output, long cacheRead, long cacheWrite,
        long webSearches = 0)
    {
        var tokens = AiPrices.Usd(model, input, output, cacheRead, cacheWrite);
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
            WebSearches = webSearches,
            // The search fee is known even for a model whose token price is not.
            Usd = tokens is null && webSearches == 0 ? null : (tokens ?? 0) + webSearches * AiPrices.WebSearchUsd,
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

    /// <summary>
    /// What today's API calls cost, as a daily cap counts it: a call on a model with no known price counts at the
    /// dearest price of its provider (<see cref="AiPrices.Ceiling"/>), never as free.
    /// </summary>
    public decimal SpentToday(string? feature = null)
    {
        var today = Local(_time.GetUtcNow()).Date;
        return Entries(feature).Where(e => Local(e.At).Date == today).Sum(e => e.Route == AgentRoute ? 0 : Counted(e));
    }

    private decimal Counted(AiUsageEntry entry)
    {
        if (entry.Usd is { } usd) return usd;
        bool first;
        lock (_unpriced) first = _unpriced.Add(entry.Model);
        if (first)
            _logger.LogWarning("No price is known for the model {Model}: its calls count against the daily cap at {Provider}'s dearest price", entry.Model, entry.Provider);
        return AiPrices.ForCap(entry);
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
