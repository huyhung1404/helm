using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Ai.Assistant;

/// <summary>
/// The model ↔ tools loop of one user turn, the same on the API and on an agent on a Helm PC: the model answers or
/// calls tools; reading runs; a change Helm can undo runs at once; one that cannot waits for a yes; a phone tool runs
/// here, or — on a PC — is handed back for the phone. Ends on an answer, a problem, or after <see cref="MaxSteps"/>.
/// </summary>
internal static class AssistantLoop
{
    public const int MaxSteps = 8;

    public sealed record Outcome(
        string Text,
        IReadOnlyList<AssistantAction> Done,
        IReadOnlyList<AiJobAction> Records,
        IReadOnlyList<AiJobPhoneAction> ForPhone,
        bool Declined,
        string? Problem);

    /// <param name="stop">Checked before each further step (the day's cap); a reason ends the turn.</param>
    /// <param name="phoneHere">False on a PC: phone tools are not run but handed back in <see cref="Outcome.ForPhone"/>.</param>
    public static async Task<Outcome> RunAsync(IAssistantChat chat, IReadOnlyList<AssistantTool> tools,
        Func<IReadOnlyList<PendingAction>, Task<bool>> confirm, Action<AssistantUsage> count, Func<string?> stop, bool phoneHere,
        string modelName, CancellationToken ct)
    {
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var done = new List<AssistantAction>();
        var records = new List<AiJobAction>();
        var forPhone = new List<AiJobPhoneAction>();
        var declined = false;
        Outcome End(string text, string? problem) => new(text, done, records, forPhone, declined, problem);

        for (var step = 0; step < MaxSteps; step++)
        {
            if (step > 0 && stop() is { } reason) return End("", reason);
            AssistantStep answer;
            try
            {
                answer = await chat.NextAsync(ct);
            }
            catch (AssistantException ex)
            {
                return End("", ex.Message);
            }
            count(answer.Usage);
            if (answer.Refused) return End("", $"{modelName} declined this request.");
            if (answer.Calls.Count == 0) return End(answer.Text, null);

            var waiting = answer.Calls.Where(c => byName.TryGetValue(c.Name, out var t) && t.Mode == AssistantToolMode.Confirm).ToList();
            var approved = waiting.Count == 0 || await confirm([.. waiting.Select(c => new PendingAction(c.Name, Summary(c)))]);
            if (!approved) declined = true;

            var results = new List<ToolOutcome>();
            foreach (var call in answer.Calls)
            {
                if (!byName.TryGetValue(call.Name, out var tool))
                {
                    results.Add(new ToolOutcome(call.Id, $"There is no tool {call.Name}.", true));
                    continue;
                }
                if (tool.Mode == AssistantToolMode.Confirm && !approved)
                {
                    results.Add(new ToolOutcome(call.Id, "The user said no: nothing was done. Do not try again; tell them it was not done.", false));
                    continue;
                }
                var input = WithoutSummary(call.Input);
                if (!phoneHere && tool.Name.StartsWith(HelmAssistant.PhoneToolPrefix, StringComparison.Ordinal))
                {
                    forPhone.Add(new AiJobPhoneAction(tool.Name, input.GetRawText(), Summary(call)));
                    results.Add(new ToolOutcome(call.Id, """{"handed_to_phone":true}""", false));
                    continue;
                }
                try
                {
                    var json = HelmAssistant.Serialize(await tool.Source.Run(input, ct));
                    if (tool.Mode != AssistantToolMode.Read)
                    {
                        Func<CancellationToken, Task>? undo = null;
                        if (tool.Source.Undo is { } takeBack)
                        {
                            var result = JsonDocument.Parse(json).RootElement.Clone();
                            undo = c => takeBack(result, c);
                        }
                        done.Add(new AssistantAction(call.Name, Summary(call), undo));
                        records.Add(new AiJobAction(call.Name, Summary(call), json));
                    }
                    results.Add(new ToolOutcome(call.Id, json, false));
                }
                catch (McpToolException ex)
                {
                    results.Add(new ToolOutcome(call.Id, ex.Message, true));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new ToolOutcome(call.Id, $"It failed: {ex.Message}", true));
                }
            }
            chat.AddResults(results);
        }
        return End("Em dừng lại vì việc này cần quá nhiều bước. Anh nói ngắn hơn giúp em nha.", null);
    }

    internal static string Summary(ToolCall call) =>
        call.Input.ValueKind == JsonValueKind.Object && call.Input.TryGetProperty(AssistantTool.SummaryArgument, out var s) && s.GetString() is { Length: > 0 } text
            ? text.Trim()
            : call.Name;

    private static JsonElement WithoutSummary(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(AssistantTool.SummaryArgument, out _)) return input;
        var copy = JsonNode.Parse(input.GetRawText())!.AsObject();
        copy.Remove(AssistantTool.SummaryArgument);
        return JsonDocument.Parse(copy.ToJsonString()).RootElement.Clone();
    }
}
