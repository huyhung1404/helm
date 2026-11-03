using Helm.Core.Sync;

namespace Helm.Ai.Assistant;

/// <summary>One question to a model with no tools: a system prompt and a text, answered in text (often JSON).</summary>
/// <param name="Feature">An <see cref="AiFeatureIds"/> id: the choice of model and route, the cap and the costs go by it.</param>
public sealed record AiOneShotRequest(string Feature, string System, string Prompt)
{
    /// <summary>
    /// The user asked for it (a tap): it may go to the API as the feature's route allows. A call that runs by itself
    /// goes to an agent on a Helm PC only, unless the user chose a route for its feature.
    /// </summary>
    public bool UserStarted { get; init; }
}

/// <param name="Text">The model's answer; null when nothing answered (<see cref="Problem"/> says why).</param>
/// <param name="RanOn">The agent and Helm PC that answered ("Claude Code trên PC-HUNG"); null when the API did.</param>
public sealed record AiOneShotAnswer(string? Text, string? Problem, string? RanOn = null);

/// <summary>
/// Helm's features that need one answer, not a conversation (file a transaction, read a bank notification): agents on
/// a Helm PC first (free), the API last, within the feature's daily cap. A feature with no choice of its own takes Ask
/// Helm's model, and runs on a PC only when it was not started by the user. Every call is counted in
/// <see cref="AiUsageBook"/> under its feature.
/// </summary>
public sealed class AiOneShot(
    AiKeyStore keys,
    ISyncedSettings<AiFeatureSettings> features,
    AiUsageBook usage,
    IEnumerable<IAssistantChatFactory> factories,
    AiRouter router)
{
    /// <summary>How long an agent on this very device may take (one through sync waits its route's own way).</summary>
    internal static readonly TimeSpan LocalAgentLimit = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IAssistantChatFactory> _factories = [.. factories];

    /// <summary>The feature's own choice, else Ask Helm's model with a route that suits who started it.</summary>
    public AiFeatureChoice ChoiceFor(string feature, bool userStarted)
    {
        if (features.Current.Features.TryGetValue(feature, out var own)) return own;
        var inherited = features.Current.For(AiFeatureIds.AskHelm).Copy();
        inherited.Enabled = true;
        // The owner's rule: what runs by itself never spends on the API unless the user said so.
        if (!userStarted) inherited.Route = AiRoutePolicy.PcOnly;
        return inherited;
    }

    public async Task<AiOneShotAnswer> AskAsync(AiOneShotRequest request, CancellationToken ct)
    {
        var choice = ChoiceFor(request.Feature, request.UserStarted);
        if (!choice.Enabled) return new AiOneShotAnswer(null, "This AI feature is turned off.");
        var conversation = Guid.NewGuid().ToString("N");
        var job = new AiJob(request.Feature, conversation, request.Prompt, [], [], choice.Model, choice.Effort) { System = request.System };
        foreach (var route in router.Agents(choice))
        {
            try
            {
                var wait = route is SyncAgentRoute ? TimeSpan.FromSeconds(Math.Max(1, choice.PcWaitSeconds)) : LocalAgentLimit;
                var result = await route.RunAsync(job, wait, ct);
                usage.RecordAgent(request.Feature, result.Agent ?? "agent", choice.Model, route.Device, result.SavedUsd);
                return new AiOneShotAnswer(result.Text, null, result.Agent is { } agent ? $"{agent} trên {route.Device}" : route.Device);
            }
            catch (AiRouteTakenException ex)
            {
                // It took the question: asking the API too would pay for an answer that may still come.
                return new AiOneShotAnswer(null, ex.Message);
            }
            catch (AiRouteException)
            {
                // The next way.
            }
            finally
            {
                route.End(conversation);
            }
        }
        if (!AiRouter.MayUseApi(choice)) return new AiOneShotAnswer(null, router.NoPc());
        return await OnApiAsync(request, choice, ct);
    }

    private async Task<AiOneShotAnswer> OnApiAsync(AiOneShotRequest request, AiFeatureChoice choice, CancellationToken ct)
    {
        if (usage.SpentToday(request.Feature) >= choice.DailyCapUsd) return new AiOneShotAnswer(null, "Today's limit for this AI feature on the API is reached.");
        var provider = AiProviders.Find(choice.Provider) ?? AiProviders.Claude;
        if (keys.DefaultFor(provider) is not { } key || _factories.FirstOrDefault(f => f.Provider == provider) is not { } factory)
            return new AiOneShotAnswer(null, $"No Helm PC answered, and there is no {provider.Name} API key to fall back on.");
        var chat = factory.Create(key.Value.Secret, choice.Model, choice.Effort, request.System, []);
        try
        {
            chat.AddUser(request.Prompt);
            var step = await chat.NextAsync(ct);
            var u = step.Usage;
            usage.Record(request.Feature, provider, choice.Model, key.Id, u.Input, u.Output, u.CacheRead, u.CacheWrite);
            return step.Refused ? new AiOneShotAnswer(null, $"{choice.Model} declined this request.") : new AiOneShotAnswer(step.Text, null);
        }
        catch (AssistantException ex)
        {
            return new AiOneShotAnswer(null, ex.Message);
        }
        finally
        {
            (chat as IDisposable)?.Dispose();
        }
    }
}
