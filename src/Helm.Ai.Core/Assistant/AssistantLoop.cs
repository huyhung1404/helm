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

    /// <summary>The answer when a step only made changes and all of them went through: the card lists each with Undo.</summary>
    public const string DoneText = "Em làm xong rồi nha Anh.";

    public sealed record Outcome(
        string Text,
        IReadOnlyList<AssistantAction> Done,
        IReadOnlyList<AiJobAction> Records,
        IReadOnlyList<AiJobPhoneAction> ForPhone,
        bool Declined,
        string? Problem);

    /// <param name="stop">Checked before each further step (the day's cap); a reason ends the turn.</param>
    /// <param name="phoneHere">False on a PC: phone tools are not run but handed back in <see cref="Outcome.ForPhone"/>.</param>
    /// <param name="partial">The answer's text so far while a streaming model writes it (<see cref="IAssistantChat.NextAsync(Action{string}?, CancellationToken)"/>).</param>
    /// <param name="timings">Marked after each model call and each step's tools, for the Debug log.</param>
    public static async Task<Outcome> RunAsync(IAssistantChat chat, IReadOnlyList<AssistantTool> tools,
        Func<IReadOnlyList<PendingAction>, Task<bool>> confirm, Action<AssistantUsage> count, Func<string?> stop, bool phoneHere,
        string modelName, CancellationToken ct, List<AiJobAction>? recordsInto = null, Action<string>? partial = null,
        TurnTimings? timings = null)
    {
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var done = new List<AssistantAction>();
        // The caller's list when it gave one: a turn stopped midway (too long, cancelled) still tells what it did.
        var records = recordsInto ?? [];
        var forPhone = new List<AiJobPhoneAction>();
        var declined = false;
        Outcome End(string text, string? problem) => new(text, done, records, forPhone, declined, problem);

        for (var step = 0; step < MaxSteps; step++)
        {
            if (step > 0 && stop() is { } reason) return End("", reason);
            AssistantStep answer;
            try
            {
                answer = await chat.NextAsync(partial, ct);
            }
            catch (AssistantException ex)
            {
                timings?.Mark("model (failed)");
                return End("", ex.Message);
            }
            timings?.Mark(answer.Calls.Count == 0 ? "model" : $"model ({answer.Calls.Count} calls)");
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
                        // One call may make several records (a bill split among friends): each is a row with its own Undo.
                        foreach (var (summary, record) in Records(json, Summary(call)))
                        {
                            Func<CancellationToken, Task>? undo = null;
                            // A record that changed nothing (a task done already) has nothing to take back.
                            if (tool.Source.Undo is { } takeBack && !McpTool.ChangedNothing(record))
                            {
                                var result = JsonDocument.Parse(record).RootElement.Clone();
                                undo = c => takeBack(result, c);
                            }
                            done.Add(new AssistantAction(call.Name, summary, undo));
                            records.Add(new AiJobAction(call.Name, summary, record));
                        }
                    }
                    // The records and Undo keep the whole result; the model reads only what it needs.
                    results.Add(new ToolOutcome(call.Id, ForModel(tool.Source, json), false));
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
            timings?.Mark("tools");
            chat.AddResults(results);
            // A step that only recorded things (no reading, nothing waiting for a yes, nothing failed) needs no second
            // call to say so: the summaries are the answer. Half the time and one call fewer for "phở 45k".
            if (answer.Calls.All(c => byName.TryGetValue(c.Name, out var t) && t.Mode == AssistantToolMode.Auto) && results.All(r => !r.IsError))
                return End(DoneText, null);
        }
        return End("Em dừng lại vì việc này cần quá nhiều bước. Anh nói ngắn hơn giúp em nha.", null);
    }

    /// <summary>
    /// What a change call recorded, as the card lists it: each item of the result's <see cref="McpTool.RecordsProperty"/>
    /// list (with its own summary, and given alone to Undo) when it has one, else the call itself.
    /// </summary>
    internal static IEnumerable<(string Summary, string Json)> Records(string json, string summary)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return [(summary, json)];
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(McpTool.RecordsProperty, out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return [(summary, json)];
        return [.. list.EnumerateArray().Select(r => (
            r.ValueKind == JsonValueKind.Object && r.TryGetProperty(AssistantTool.SummaryArgument, out var s) && s.GetString() is { Length: > 0 } line ? line : summary,
            r.GetRawText()))];
    }

    /// <summary>A result as the model gets it: shaped by the tool's <see cref="McpTool.BriefResult"/> when it has one.</summary>
    internal static string ForModel(McpTool tool, string json)
    {
        if (tool.BriefResult is not { } brief) return json;
        try
        {
            return JsonNode.Parse(json) is { } node ? HelmAssistant.Serialize(brief(node)) : json;
        }
        catch (JsonException)
        {
            return json;
        }
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
