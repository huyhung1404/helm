namespace Helm.Ai;

/// <summary>A route as the settings offer it: a short label for the table, a line saying what it means.</summary>
public sealed record AiRouteOption(AiRoutePolicy Policy, string Label, string Detail)
{
    public static IReadOnlyList<AiRouteOption> All { get; } =
    [
        new(AiRoutePolicy.PreferPc, "Agents → API", "An agent on your PC first, the API key if no PC answers"),
        new(AiRoutePolicy.PcOnly, "Agents only", "Waits for a PC agent. Always 0đ"),
        new(AiRoutePolicy.ApiOnly, "API only", "Fastest. Uses the API key and costs money"),
    ];

    public static AiRouteOption For(AiRoutePolicy policy) => All.FirstOrDefault(r => r.Policy == policy) ?? All[0];
}

/// <summary>How hard a Claude model thinks (<see cref="AiFeatureChoice.Effort"/>).</summary>
public sealed record AiEffortOption(string Id, string Label)
{
    /// <summary>The card's slider levels (<see cref="AiEfforts.Levels"/>); a model that takes fewer gets the nearest it takes.</summary>
    public static IReadOnlyList<AiEffortOption> All { get; } = [.. AiEfforts.Levels.Select(l => new AiEffortOption(l, AiEfforts.Name(l)))];

    public static AiEffortOption For(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}
