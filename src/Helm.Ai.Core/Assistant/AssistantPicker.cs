using System.Globalization;

namespace Helm.Ai.Assistant;

/// <summary>Where a pick in the card's model sheet runs: Auto (PC agents first, then the API), one PC agent, or the API.</summary>
public enum AssistantPickKind
{
    Auto,
    Pc,
    Api,
}

/// <summary>One version in the card's model sheet: a model an agent on a PC runs, or one a key reaches.</summary>
/// <param name="Agent">The PC agent's name (<see cref="AssistantPickKind.Pc"/>); null on the API.</param>
/// <param name="Model">The model id; "" for an agent that runs only its own (Codex).</param>
public sealed record AssistantPick(AssistantPickKind Kind, string? Agent, AiProvider Provider, string Model, string Name)
{
    /// <summary>About what one question costs on the API ("~60đ"); null on a PC (free) or when Helm knows no price.</summary>
    public string? Price { get; init; }

    /// <summary>The effort levels it takes (<see cref="AiEfforts.Levels"/>); empty: the slider does nothing for it.</summary>
    public IReadOnlyList<string> Efforts { get; init; } = [];
}

/// <summary>A row of the sheet that opens to its versions: an agent on a PC, or one family of a provider's models.</summary>
public sealed record AssistantPickFamily(AssistantPickKind Kind, string Name, IReadOnlyList<AssistantPick> Versions)
{
    /// <summary>The PC the agent runs on.</summary>
    public string? Device { get; init; }

    /// <summary>Why the agent takes no turn now (the PC is off, it is signed out); null when it does.</summary>
    public string? Problem { get; init; }
}

/// <summary>One step of Auto, in the order it is tried, and why it would be passed over now (null: it would answer).</summary>
public sealed record AssistantAutoStep(AssistantPickKind Kind, string Name, string? Problem);

/// <summary>What the card's model sheet shows: Auto's steps, the families, the pick (null: Auto) and the effort.</summary>
public sealed record AssistantPicker(IReadOnlyList<AssistantAutoStep> Auto, IReadOnlyList<AssistantPickFamily> Families, AssistantPick? Picked, string Effort)
{
    /// <summary>The effort levels of what would answer now: the pick's, or Auto's first step that would answer.</summary>
    public IReadOnlyList<string> Efforts { get; init; } = AiEfforts.Levels;
}

/// <summary>
/// Builds the model sheet from what the PCs' agents offer and the models the keys reach (<see cref="AssistantPicker"/>),
/// and turns a pick back into the feature's choice (<see cref="Apply"/>).
/// </summary>
public static class AssistantPicks
{
    /// <summary>About the tokens of one Ask Helm question on the API (instructions and tools in, a short answer out).</summary>
    private const int TypicalInput = 3000, TypicalOutput = 300;

    /// <summary>đồng per US dollar (late 2026), for the sheet's rough price of a question only; usage is counted in dollars.</summary>
    private const decimal DongPerDollar = 26_000m;

    /// <param name="api">The models the keys reach (<see cref="HelmAssistant.Models"/>).</param>
    public static AssistantPicker Build(AiFeatureChoice choice, IReadOnlyList<AiAgentOffer> offers, IReadOnlyList<AssistantModelOption> api)
    {
        var families = new List<AssistantPickFamily>();
        foreach (var offer in offers)
        {
            var agent = offer.Agent;
            IReadOnlyList<AssistantPick> versions = agent.Models.Count == 0
                ? [new AssistantPick(AssistantPickKind.Pc, agent.Name, AiProviders.Claude, "", agent.Name)]
                : [.. Sorted(agent.Models).Select(m => new AssistantPick(AssistantPickKind.Pc, agent.Name, ProviderOf(m), m, Name(m)) { Efforts = AiEfforts.For(m, agent.Kind) })];
            families.Add(new AssistantPickFamily(AssistantPickKind.Pc, agent.Name, versions) { Device = offer.Device, Problem = agent.Problem });
        }
        foreach (var family in api.GroupBy(m => FamilyOf(m.Provider, m.Model)).OrderBy(g => g.Key.Order).ThenBy(g => g.Key.Name, StringComparer.Ordinal))
        {
            var versions = Sorted(family.Select(m => m.Model)).Select(id => family.First(m => m.Model == id))
                .Select(m => new AssistantPick(AssistantPickKind.Api, null, m.Provider, m.Model, Name(m.Model)) { Price = PriceOf(m.Model), Efforts = AiEfforts.For(m.Model) });
            families.Add(new AssistantPickFamily(AssistantPickKind.Api, family.Key.Name, [.. versions]));
        }

        var picked = Picked(choice, families);
        var auto = AutoSteps(choice, offers, api);
        var efforts = picked?.Efforts ?? AutoEfforts(choice, offers, api);
        return new AssistantPicker(auto, families, picked, choice.Effort) { Efforts = efforts };
    }

    /// <summary>The choice that runs <paramref name="pick"/> (null: Auto), keeping what the pick does not say.</summary>
    public static void Apply(AiFeatureChoice choice, AssistantPick? pick)
    {
        switch (pick?.Kind)
        {
            case null or AssistantPickKind.Auto:
                choice.Route = AiRoutePolicy.PreferPc;
                choice.Agent = null;
                break;
            case AssistantPickKind.Pc:
                choice.Route = AiRoutePolicy.PcOnly;
                choice.Agent = pick.Agent;
                if (pick.Model.Length > 0)
                {
                    choice.Provider = pick.Provider.Id;
                    choice.Model = pick.Model;
                }
                break;
            case AssistantPickKind.Api:
                choice.Route = AiRoutePolicy.ApiOnly;
                choice.Agent = null;
                choice.Provider = pick.Provider.Id;
                choice.Model = pick.Model;
                break;
        }
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5", "grok-4.7-build-fast" → "Grok 4.7 build fast".</summary>
    public static string Name(string id)
    {
        var parts = id.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return id;
        if (parts.Count > 1 && parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        if (parts.Count > 1 && parts[^1].Length == 8 && parts[^1].All(char.IsAsciiDigit)) parts.RemoveAt(parts.Count - 1);
        var text = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        for (var i = 1; i < parts.Count; i++)
            text += (IsNumber(parts[i]) && IsNumber(parts[i - 1]) ? "." : " ") + parts[i];
        return text;

        static bool IsNumber(string part) => part.All(c => char.IsAsciiDigit(c) || c == '.');
    }

    private static AssistantPick? Picked(AiFeatureChoice choice, IReadOnlyList<AssistantPickFamily> families)
    {
        if (AiRouter.PickedAgent(choice) is { } agent)
        {
            var family = families.FirstOrDefault(f => f.Kind == AssistantPickKind.Pc && Same(f.Name, agent));
            return family?.Versions.FirstOrDefault(v => v.Model == choice.Model) ?? family?.Versions.FirstOrDefault()
                // An agent whose PC is not online now: still the pick, as the user made it.
                ?? new AssistantPick(AssistantPickKind.Pc, agent, ProviderOf(choice.Model), choice.Model, Name(choice.Model));
        }
        if (choice.Route != AiRoutePolicy.ApiOnly) return null;
        return families.Where(f => f.Kind == AssistantPickKind.Api).SelectMany(f => f.Versions).FirstOrDefault(v => v.Provider.Id == choice.Provider && v.Model == choice.Model)
            ?? new AssistantPick(AssistantPickKind.Api, null, AiProviders.Find(choice.Provider) ?? AiProviders.Claude, choice.Model, Name(choice.Model))
            { Price = PriceOf(choice.Model), Efforts = AiEfforts.For(choice.Model) };
    }

    /// <summary>Auto: every agent on the PCs in their order, each on its own model, then the API on the choice's model.</summary>
    private static IReadOnlyList<AssistantAutoStep> AutoSteps(AiFeatureChoice choice, IReadOnlyList<AiAgentOffer> offers, IReadOnlyList<AssistantModelOption> api)
    {
        var steps = offers.Select(o => new AssistantAutoStep(AssistantPickKind.Pc, AgentStepName(o.Agent, choice.Model), o.Agent.Problem)).ToList();
        var model = ApiModel(choice, api);
        steps.Add(new AssistantAutoStep(AssistantPickKind.Api, Name(model?.Model ?? choice.Model), model is null ? "no API key" : null));
        return steps;
    }

    private static IReadOnlyList<string> AutoEfforts(AiFeatureChoice choice, IReadOnlyList<AiAgentOffer> offers, IReadOnlyList<AssistantModelOption> api)
    {
        if (offers.FirstOrDefault(o => o.Agent.Problem is null) is { } first) return AiEfforts.For(ModelOf(first.Agent, choice.Model) ?? "", first.Agent.Kind);
        return ApiModel(choice, api) is { } model ? AiEfforts.For(model.Model) : AiEfforts.Levels;
    }

    /// <summary>The API model Auto ends on: the choice's when a key reaches it, else the first a key reaches.</summary>
    private static AssistantModelOption? ApiModel(AiFeatureChoice choice, IReadOnlyList<AssistantModelOption> api) =>
        api.FirstOrDefault(m => m.Provider.Id == choice.Provider && m.Model == choice.Model) ?? api.FirstOrDefault(m => m.Provider.Id == choice.Provider) ?? api.FirstOrDefault();

    /// <summary>"Grok 4.7 build fast" for a Grok agent asked for a Claude model: what it runs then (as LocalAgentRoute does).</summary>
    private static string AgentStepName(AiWorkerAgent agent, string requested) =>
        ModelOf(agent, requested) is { } model ? $"{agent.Name} · {Name(model)}" : agent.Name;

    private static string? ModelOf(AiWorkerAgent agent, string requested)
    {
        if (agent.Models.Count == 0) return null;
        var prefix = agent.Kind switch { "claude-code" => "claude-", "grok" => "grok-", _ => null };
        return agent.Models.Contains(requested) || (prefix is not null && requested.StartsWith(prefix, StringComparison.Ordinal)) ? requested : agent.Models[0];
    }

    private static string? PriceOf(string model)
    {
        if (AiPrices.For(model) is not { } price) return null;
        var dong = (price.Input * TypicalInput + price.Output * TypicalOutput) / 1_000_000m * DongPerDollar;
        // Two significant figures: "~60đ", "~420đ", "~1.100đ".
        var magnitude = (decimal)Math.Pow(10, Math.Max(0, Math.Floor(Math.Log10((double)Math.Max(dong, 1m))) - 1));
        var rounded = Math.Max(1m, Math.Round(dong / magnitude) * magnitude);
        return "~" + rounded.ToString("#,0", CultureInfo.GetCultureInfo("vi-VN")) + "đ";
    }

    private static AiProvider ProviderOf(string model) => model.StartsWith("grok-", StringComparison.Ordinal) ? AiProviders.Grok : AiProviders.Claude;

    private static (int Order, string Name) FamilyOf(AiProvider provider, string id)
    {
        if (provider == AiProviders.Grok) return (9, "Grok (xAI)");
        var parts = id.Split('-');
        var family = parts.Length > 1 && parts[0] == "claude" ? parts[1] : id;
        return family switch
        {
            "opus" => (0, "Claude Opus"),
            "fable" => (1, "Claude Fable"),
            "sonnet" => (2, "Claude Sonnet"),
            "haiku" => (3, "Claude Haiku"),
            _ when family.Length > 0 && char.IsAsciiLetter(family[0]) => (5, "Claude " + char.ToUpperInvariant(family[0]) + family[1..]),
            _ => (6, "Claude"),
        };
    }

    /// <summary>Newest first ("opus-5-5" before "opus-5" before "opus-4-8"), the plain name before its variants.</summary>
    private static IEnumerable<string> Sorted(IEnumerable<string> models) =>
        models.Distinct(StringComparer.Ordinal).OrderByDescending(m => m, Comparer<string>.Create(CompareVersions));

    private static int CompareVersions(string a, string b)
    {
        var x = Numbers(a);
        var y = Numbers(b);
        for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        if (x.Count != y.Count) return x.Count.CompareTo(y.Count);
        // Same numbers: the shorter id ("grok-4.7") is the plain one, listed first.
        return b.Length.CompareTo(a.Length);
    }

    /// <summary>The version numbers of an id, without a date snapshot: "claude-opus-4-1-20250805" → 4, 1.</summary>
    private static List<int> Numbers(string id) =>
        [.. id.Split('-', '.').Where(p => p.Length is > 0 and < 8 && p.All(char.IsAsciiDigit)).Select(int.Parse)];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);
}
