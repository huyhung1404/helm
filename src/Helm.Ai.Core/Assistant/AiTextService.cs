using System.Globalization;
using Helm.Core.Ai;
using Helm.Core.Sync;

namespace Helm.Ai.Assistant;

/// <summary>
/// <see cref="IAiText"/> on the router: an agent on a Helm PC first (a text-only job, no tools), then the API with the
/// feature's model, within its daily cap. A feature with no choice of its own uses Ask Helm's (the model picked in the
/// card). Nothing a text call does changes data, so a PC that took it and failed is no reason not to try the API.
/// </summary>
public sealed class AiTextService(
    AiKeyStore keys,
    ISyncedSettings<AiFeatureSettings> features,
    AiUsageBook usage,
    IEnumerable<IAssistantChatFactory> factories,
    AiRouter router) : IAiText
{
    /// <summary>How long an agent on this device may take for a text call (a word's analysis is a long answer).</summary>
    internal static readonly TimeSpan LocalRun = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IAssistantChatFactory> _factories = [.. factories];

    /// <summary>The feature's own choice, else Ask Helm's (on: the feature is on while it has no choice of its own).</summary>
    public AiFeatureChoice ChoiceFor(string feature)
    {
        var all = features.Current;
        if (all.Features.TryGetValue(feature, out var own)) return own;
        var shared = all.For(AiFeatureIds.AskHelm).Copy();
        shared.Enabled = true;
        return shared;
    }

    public string? Unavailable(string feature)
    {
        var choice = ChoiceFor(feature);
        if (!choice.Enabled) return "This AI feature is turned off: AI → Features on the PC.";
        if (router.Agents(choice).Count > 0) return null;
        if (!AiRouter.MayUseApi(choice)) return router.NoPc();
        return Api(choice) is null
            ? "No Helm PC with an agent is online, and there is no API key: add an agent on the PC (AI → Proxy) or a Claude or Grok key (AI → Models & keys)."
            : null;
    }

    public async Task<AiTextAnswer> AskAsync(AiTextRequest request, CancellationToken ct)
    {
        var choice = ChoiceFor(request.Feature);
        if (!choice.Enabled) return AiTextAnswer.Failed("This AI feature is turned off: AI → Features on the PC.");
        var model = choice.Model;

        foreach (var route in router.Agents(choice))
        {
            var conversation = Guid.NewGuid().ToString("N");
            var job = new AiJob(request.Feature, conversation, request.Prompt, [], [], model, choice.Effort) { System = request.System };
            try
            {
                // Through sync the wait is for a PC to claim it (then it has its time); an agent on this device runs within it.
                var wait = route is SyncAgentRoute ? TimeSpan.FromSeconds(Math.Max(1, choice.PcWaitSeconds)) : LocalRun;
                var result = await route.RunAsync(job, wait, ct);
                usage.RecordAgent(request.Feature, result.Agent ?? "agent", model, route.Device, result.SavedUsd);
                if (result.Text.Trim().Length > 0)
                    return new AiTextAnswer(result.Text.Trim(), null, result.Agent is { } agent ? $"{agent} trên {route.Device}" : route.Device);
            }
            catch (AiRouteException)
            {
                // Nothing was changed: the next agent, or the API, may answer instead.
            }
            finally
            {
                route.End(conversation);
            }
        }

        if (!AiRouter.MayUseApi(choice)) return AiTextAnswer.Failed(router.NoPc());
        if (Api(choice) is not { } api)
            return AiTextAnswer.Failed("No Helm PC answered in time, and there is no Claude or Grok API key to fall back on.");
        if (usage.SpentToday(request.Feature) >= choice.DailyCapUsd)
            return AiTextAnswer.Failed($"Today's limit for this feature on the API (${choice.DailyCapUsd.ToString("0.00", CultureInfo.InvariantCulture)}) is reached. It starts again tomorrow, or raise it on the PC.");

        var (factory, key, keyId, apiModel) = api;
        try
        {
            var chat = factory.Create(key, apiModel, choice.Effort, request.System, []);
            chat.AddUser(request.Prompt);
            var step = await chat.NextAsync(ct);
            var entry = usage.Record(request.Feature, factory.Provider, apiModel, keyId, step.Usage.Input, step.Usage.Output, step.Usage.CacheRead, step.Usage.CacheWrite);
            (chat as IDisposable)?.Dispose();
            if (step.Refused) return AiTextAnswer.Failed($"{apiModel} declined this request.");
            return step.Text.Trim().Length > 0
                ? new AiTextAnswer(step.Text.Trim(), null) { Model = apiModel, Usd = entry.Usd }
                : AiTextAnswer.Failed("The model gave no answer. Try again.");
        }
        catch (AssistantException ex)
        {
            return AiTextAnswer.Failed(ex.Message);
        }
    }

    /// <summary>The API to fall back on: the choice's provider when there is a key for it, else any provider with a key.</summary>
    private (IAssistantChatFactory Factory, string Key, string KeyId, string Model)? Api(AiFeatureChoice choice)
    {
        var chosen = AiProviders.Find(choice.Provider);
        foreach (var provider in AiProviders.All.OrderBy(p => p == chosen ? 0 : 1))
        {
            if (keys.DefaultFor(provider) is not { } key || _factories.FirstOrDefault(f => f.Provider == provider) is not { } factory) continue;
            var model = provider == chosen ? choice.Model : provider == AiProviders.Grok ? GrokFallback : ClaudeModels.Haiku;
            return (factory, key.Value.Secret, key.Id, model);
        }
        return null;
    }

    private const string GrokFallback = "grok-4.7";
}
