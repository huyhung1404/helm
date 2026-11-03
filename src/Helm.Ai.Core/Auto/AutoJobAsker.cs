using Helm.Ai.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Auto;

/// <summary>
/// Asks for an automatic job's text: this PC's agents (free on the user's plans), text only, no tool. The paid API
/// only when the user set that feature's route to allow it (an automatic job is <see cref="AiRoutePolicy.PcOnly"/>
/// until then), with a key, and within the feature's daily cap.
/// </summary>
public sealed class AutoJobAsker(IAgentRoute? local, AiKeyStore? keys, IEnumerable<IAssistantChatFactory> factories, AiUsageBook usage,
    ILogger? logger = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    internal const string System = """
        You write the short briefs Helm, the user's own toolkit, sends to his phone by itself, from his own data given
        with each request.
        - Write in Vietnamese, warm and plain, calling the user "Anh" and yourself "em".
        - Plain text for a phone notification: no markdown, no headings, no tables; short lines, no more than the request says.
        - Use only the data given. Never invent tasks, amounts, names or dates.
        - Amounts are Vietnamese đồng: write them like 45.000đ or 1,2 triệu.
        - Answer with the brief only, nothing before or after it, and no question at the end.
        """;

    private readonly IReadOnlyList<IAssistantChatFactory> _factories = [.. factories];
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>An agent on this PC can take a job now.</summary>
    public bool AgentReady => local is not null && local.Unavailable() is null;

    /// <summary>Whether the user let this feature use the API when no agent answers (off for every automatic job by default).</summary>
    public static bool MayUseApi(AiFeatureChoice choice) => choice.Route != AiRoutePolicy.PcOnly;

    /// <summary>Someone can answer now: an agent here, or the API where the user allowed it.</summary>
    public bool CanAsk(string feature, AiFeatureChoice choice) => AgentReady || (MayUseApi(choice) && Api(feature, choice) is not null);

    /// <exception cref="AutoJobUnansweredException">No agent answered, and the API may not be used.</exception>
    public async Task<string> AskAsync(string feature, AiFeatureChoice choice, string prompt, CancellationToken ct)
    {
        if (AgentReady)
        {
            var conversation = "auto-" + Guid.NewGuid().ToString("N");
            try
            {
                var job = new AiJob(feature, conversation, prompt, [], [], choice.Model, choice.Effort) { System = System };
                var result = await local!.RunAsync(job, Timeout, ct).ConfigureAwait(false);
                usage.RecordAgent(feature, result.Agent ?? "agent", choice.Model, local.Device, result.SavedUsd);
                if (result.Text.Trim().Length > 0) return result.Text.Trim();
            }
            catch (AiRouteException ex)
            {
                _logger.LogInformation("Auto job {Feature}: this PC's agent did not answer ({Reason})", feature, ex.Message);
            }
            finally
            {
                local!.End(conversation);
            }
        }
        if (!MayUseApi(choice) || Api(feature, choice) is not { } api)
            throw new AutoJobUnansweredException(local?.Unavailable() ?? "No agent on this PC answered.");
        var (factory, provider, key) = api;
        try
        {
            var chat = factory.Create(key.Value.Secret, choice.Model, choice.Effort, System, []);
            chat.AddUser(prompt);
            var step = await chat.NextAsync(ct).ConfigureAwait(false);
            var u = step.Usage;
            usage.Record(feature, provider, choice.Model, key.Id, u.Input, u.Output, u.CacheRead, u.CacheWrite);
            if (step.Text.Trim().Length == 0) throw new AutoJobUnansweredException("The API gave no text.");
            return step.Text.Trim();
        }
        catch (AssistantException ex)
        {
            throw new AutoJobUnansweredException(ex.Message);
        }
    }

    private (IAssistantChatFactory Factory, AiProvider Provider, Core.Sync.SyncedItem<AiKey> Key)? Api(string feature, AiFeatureChoice choice)
    {
        if (AiProviders.Find(choice.Provider) is not { } provider || keys?.DefaultFor(provider) is not { } key) return null;
        if (_factories.FirstOrDefault(f => f.Provider == provider) is not { } factory) return null;
        return usage.SpentToday(feature) < choice.DailyCapUsd ? (factory, provider, key) : null;
    }
}
