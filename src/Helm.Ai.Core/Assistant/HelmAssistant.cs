using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Sync;

namespace Helm.Ai.Assistant;

/// <summary>A model the user can pick in the card: a provider they have a key for, and one of its chat models.</summary>
public sealed record AssistantModelOption(AiProvider Provider, string Model, string Name);

/// <summary>A call that waits for the user's yes (it changes something Helm cannot take back).</summary>
public sealed record PendingAction(string Tool, string Summary);

/// <summary>Something the assistant did, as the card lists it; <see cref="CanUndo"/> when Helm can take it back.</summary>
public sealed class AssistantAction(string tool, string summary, Func<CancellationToken, Task>? undo)
{
    public string Tool { get; } = tool;
    public string Summary { get; } = summary;
    public bool CanUndo => undo is not null && !Undone;
    public bool Undone { get; private set; }

    public async Task UndoAsync(CancellationToken ct)
    {
        if (undo is null || Undone) return;
        await undo(ct);
        Undone = true;
    }
}

/// <param name="Problem">Why nothing could be asked (no key, the day's cap, the provider failed); null when it worked.</param>
/// <param name="RanOn">The agent and Helm PC that answered (free, "Codex trên PC-HUNG"); null when the API did.</param>
public sealed record AssistantReply(string Text, IReadOnlyList<AssistantAction> Done, bool Declined, string? Problem, string? RanOn = null);

/// <summary>
/// Ask Helm's brain: the user's words go to Claude Code on a Helm PC when the feature's route allows and one answers in
/// time (free), else to the model the user picked through the API, with Helm's own tools (the ones AI agents get over
/// MCP, plus the phone's). Reading runs freely; a change Helm can undo runs at once and is listed with Undo; any other
/// change waits for the user's yes. Every call is counted in <see cref="AiUsageBook"/>, and the day's cap stops the API.
/// </summary>
public sealed class HelmAssistant(
    AiKeyStore keys,
    ISyncedSettings<AiFeatureSettings> features,
    AiUsageBook usage,
    IEnumerable<IMcpToolProvider> providers,
    IEnumerable<IAssistantChatFactory> factories,
    Func<string?, bool> isToolOn,
    AiRouter? router = null,
    TimeProvider? time = null)
{
    /// <summary>The tools of Helm's own tools that suit a quick spoken request (no analysis pipelines, no servers).</summary>
    public static readonly IReadOnlySet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
    {
        "wallet_add_transaction", "wallet_add_debt", "wallet_debts", "wallet_summary",
        "tracker_lists", "tracker_tasks", "tracker_add_task", "tracker_complete_task", "tracker_start_task",
        "tracker_add_tasks",
        "notes_create", "notes_search", "notes_read",
        "habits_list", "habit_report", "habit_check",
        "missions_list", "mission_get",
        "vocab_add_words", "vocab_words",
    };

    /// <summary>Tools the phone itself adds (alarm, calendar, clipboard…) start with this.</summary>
    public const string PhoneToolPrefix = "phone_";

    internal const string Instructions = """
        You are Helm's assistant on the user's phone. The user speaks Vietnamese (sometimes English); their words come
        from speech recognition, so expect small mistakes and read them generously. Do what they ask with Helm's tools:
        spending and money in (wallet_add_transaction), debts (wallet_add_debt), tasks with due dates (tracker_add_task),
        notes, habit check-ins, words to learn, alarms, timers and calendar events on the phone; or answer questions from
        their data with the reading tools.
        - Amounts are Vietnamese đồng: 45k = 45000, 1tr2 = 1200000, "45 nghìn" = 45000.
        - Do rather than ask: fill sensible defaults (now, today, the category this description had before) and say what
          you did. Ask one short question only when something essential is missing, such as an amount.
        - Find ids with the reading tools before you change a task; never invent an id.
        - Name only categories, lists and people that the tools show; never invent one.
        - Every call that changes something takes `summary`: one short Vietnamese line the user reads, with the amount,
          what it is and when, e.g. "Chi 45.000đ · Phở · Ăn uống · hôm nay".
        - Answer in Vietnamese, one or two short sentences, warm and plain, calling the user "Anh" and yourself "em".
          No markdown, no lists: the answer is shown on a small card and may be read aloud.
        - Never ask for or repeat passwords, keys or codes.
        - Make all of a request's changes in one reply. A reply that only makes changes ends the turn: Helm shows their
          summaries to the user, so when the request also asks something, call the reading tools in that same reply.
        """;

    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AiFeatureChoice Choice => features.Current.For(AiFeatureIds.AskHelm);

    public AiUsageBook Usage => usage;

    /// <summary>The agents on Helm PCs first, the API last (<see cref="AiFeatureChoice.Route"/>).</summary>
    public AiRouter Router { get; } = router ?? new AiRouter([]);

    /// <summary>Every model the user can pick: the chat models of each provider they have a tested key for.</summary>
    public IReadOnlyList<AssistantModelOption> Models()
    {
        var options = new List<AssistantModelOption>();
        foreach (var provider in AiProviders.All)
        {
            if (keys.DefaultFor(provider) is not { } key || !factories.Any(f => f.Provider == provider)) continue;
            var models = key.Value.LastCheck?.Models.Where(m => IsChatModel(provider, m.Id)).ToList() ?? [];
            if (models.Count == 0) models = [.. Fallback(provider)];
            options.AddRange(models.Select(m => new AssistantModelOption(provider, m.Id, m.Name)));
        }
        return options;
    }

    public void Choose(AssistantModelOption option) => features.Update(s =>
    {
        var choice = s.For(AiFeatureIds.AskHelm).Copy();
        choice.Provider = option.Provider.Id;
        choice.Model = option.Model;
        s.Features[AiFeatureIds.AskHelm] = choice;
    });

    /// <summary>
    /// The model the card shows: the one chosen, else the first the user can pick. Without any key, the chosen Claude
    /// model still runs on a Helm PC's Claude Code.
    /// </summary>
    public AssistantModelOption? Current()
    {
        var all = Models();
        return all.FirstOrDefault(o => o.Provider.Id == Choice.Provider && o.Model == Choice.Model)
            ?? all.FirstOrDefault(o => o.Provider.Id == Choice.Provider)
            ?? all.FirstOrDefault()
            ?? (Router.Agents(Choice).Count > 0
                ? new AssistantModelOption(AiProviders.Claude, Choice.Provider == AiProviders.Claude.Id ? Choice.Model : ClaudeModels.Haiku, "Agent on a PC")
                : null);
    }

    /// <summary>Why Ask Helm cannot answer now, or null.</summary>
    public string? Unavailable()
    {
        if (!Choice.Enabled) return "Ask Helm is turned off: AI → Features on the PC.";
        if (Current() is null)
            return "No Helm PC with an agent is online, and there is no API key: add an agent on the PC (AI → Proxy) or a Claude or Grok key (AI → Models & keys); keys sync to this phone.";
        return null;
    }

    /// <summary>Why the API may not be used now (the day's cap), or null.</summary>
    internal string? OverCap() => usage.SpentToday(AiFeatureIds.AskHelm) >= Choice.DailyCapUsd
        ? $"Today's limit for Ask Helm on the API (${Choice.DailyCapUsd.ToString("0.00", CultureInfo.InvariantCulture)}) is reached. It starts again tomorrow, or raise it on the PC."
        : null;

    /// <param name="phone">The phone's own tools for this card (alarm, calendar, clipboard), which need its screen.</param>
    public AssistantSession Start(IEnumerable<IMcpToolProvider>? phone = null) => new(this, [.. phone ?? []]);

    internal IReadOnlyList<AssistantTool> OfferedTools(IReadOnlyList<IMcpToolProvider> phone) =>
    [
        .. providers.Concat(phone)
            .Where(p => isToolOn(p.ModuleId))
            .SelectMany(p => p.Tools)
            .Where(t => t.Risk != McpRisk.Remote && (Tools.Contains(t.Name) || t.Name.StartsWith(PhoneToolPrefix, StringComparison.Ordinal)))
            .GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First())
            .Select(t => new AssistantTool(t, ModeOf(t))),
    ];

    /// <summary>
    /// Reading runs freely; a change Helm can undo runs at once; a phone tool hands over to a phone app the user sees
    /// (Clock, Calendar), which is its confirmation; anything else waits for a yes.
    /// </summary>
    internal static AssistantToolMode ModeOf(McpTool tool) =>
        tool.ReadOnly ? AssistantToolMode.Read
        : tool.Undo is not null || tool.Name.StartsWith(PhoneToolPrefix, StringComparison.Ordinal) ? AssistantToolMode.Auto
        : AssistantToolMode.Confirm;

    internal bool HasKeyFor(AiProvider provider) => keys.DefaultFor(provider) is not null;

    internal (IAssistantChat Chat, AssistantModelOption Model, string KeyId) Open(IReadOnlyList<AssistantTool> tools)
    {
        var model = Current() ?? throw new AssistantException("No model to ask.");
        var key = keys.DefaultFor(model.Provider) ?? throw new AssistantException($"No {model.Provider.Name} API key.");
        var factory = factories.First(f => f.Provider == model.Provider);
        return (factory.Create(key.Value.Secret, model.Model, Choice.Effort, Instructions, tools), model, key.Id);
    }

    internal void Count(AssistantModelOption model, string keyId, AssistantUsage u) =>
        usage.Record(AiFeatureIds.AskHelm, model.Provider, model.Model, keyId, u.Input, u.Output, u.CacheRead, u.CacheWrite);

    internal string Now()
    {
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), TimeZoneInfo.Local);
        var offset = local.Offset;
        return $"Now: {local.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, the user's local time (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}).";
    }

    internal static string Serialize(object? result) => result as string ?? JsonSerializer.Serialize(result, Output);

    internal static bool IsChatModel(AiProvider provider, string id) =>
        provider != AiProviders.Grok || !id.Contains("imagine", StringComparison.Ordinal);

    private static IEnumerable<AiModel> Fallback(AiProvider provider) => provider == AiProviders.Grok
        ? [new AiModel("grok-4.7", "grok-4.7")]
        : [new AiModel(ClaudeModels.Haiku, "Claude Haiku 5.5"), new AiModel("claude-sonnet-5-5", "Claude Sonnet 5.5")];
}

/// <summary>
/// One conversation in the card: the user's turns and follow-ups. It goes to Claude Code on a Helm PC when the
/// feature's route allows and one answers in time (and stays there), else to the API on the picked model.
/// </summary>
public sealed class AssistantSession
{
    private readonly HelmAssistant _helm;
    private readonly IReadOnlyList<IMcpToolProvider> _phone;
    private readonly string _conversationId = Guid.NewGuid().ToString("N");
    private readonly List<(string Role, string Text)> _history = [];
    private IReadOnlyList<AssistantTool>? _all;
    private readonly HashSet<string> _scope = new(StringComparer.Ordinal);
    private IReadOnlyList<AssistantTool> _tools = [];
    private (IAssistantChat Chat, AssistantModelOption Model, string KeyId)? _open;
    private IAgentRoute? _pc;

    internal AssistantSession(HelmAssistant helm, IReadOnlyList<IMcpToolProvider> phone)
    {
        _helm = helm;
        _phone = phone;
    }

    /// <summary>The model this conversation talks to (a new pick in the card starts a new conversation).</summary>
    public AssistantModelOption? Model => _open?.Model ?? _helm.Current();

    /// <summary>The conversation is over: a Claude Code process kept for it may stop.</summary>
    public void End() => _pc?.End(_conversationId);

    /// <param name="confirm">Asked once per step for the calls that wait for a yes; false: none of them runs.</param>
    public async Task<AssistantReply> SendAsync(string text, Func<IReadOnlyList<PendingAction>, Task<bool>> confirm, CancellationToken ct)
    {
        if (_helm.Unavailable() is { } problem) return new AssistantReply("", [], false, problem);
        var first = _history.Count == 0;
        _all ??= _helm.OfferedTools(_phone);
        // Only the tools this turn's request needs (and those of earlier turns): each schema costs tokens on every call.
        var before = _tools;
        _tools = ToolScope.Pick(_all, _scope, text);
        if (_open is { } open && _tools.Where(t => before.All(b => b.Name != t.Name)).ToList() is { Count: > 0 } added) open.Chat.AddTools(added);
        var model = Model!;
        var choice = _helm.Choice;

        // Claude Code on a Helm PC first (free), unless the conversation already moved to the API.
        if (_open is null)
        {
            IReadOnlyList<IAgentRoute> routes = _pc is not null ? [_pc] : _helm.Router.Agents(choice);
            foreach (var route in routes)
            {
                if (await OnPcAsync(route, first ? $"{_helm.Now()}\n\n{text}" : text, model, choice, ct) is { } onPc)
                {
                    _history.Add(("user", text));
                    _history.Add(("assistant", onPc.Text));
                    return onPc;
                }
            }
            _pc = null;
            if (!AiRouter.MayUseApi(choice)) return new AssistantReply("", [], false, _helm.Router.NoPc());
            if (!_helm.HasKeyFor(model.Provider))
                return new AssistantReply("", [], false, $"No Helm PC answered in time, and there is no {model.Provider.Name} API key to fall back on.");
        }

        var onApi = await OnApiAsync(text, first, confirm, ct);
        _history.Add(("user", text));
        _history.Add(("assistant", onApi.Text));
        return onApi;
    }

    private async Task<AssistantReply?> OnPcAsync(IAgentRoute route, string text, AssistantModelOption model, AiFeatureChoice choice, CancellationToken ct)
    {
        static bool IsPhone(AssistantTool t) => t.Name.StartsWith(HelmAssistant.PhoneToolPrefix, StringComparison.Ordinal);
        var job = new AiJob(AiFeatureIds.AskHelm, _conversationId, text,
            [.. _tools.Where(t => !IsPhone(t)).Select(t => t.Name)], [.. _tools.Where(IsPhone).Select(t => t.Name)],
            model.Model, choice.Effort);
        AiJobResult result;
        try
        {
            result = await route.RunAsync(job, TimeSpan.FromSeconds(Math.Max(1, choice.PcWaitSeconds)), ct);
        }
        catch (AiRouteTakenException ex)
        {
            // It may have done part of it: running it again on the API could do it twice.
            _pc = route;
            return new AssistantReply("", [], false, $"{ex.Message} Em không chạy lại qua API để khỏi làm hai lần.", route.Device);
        }
        catch (AiRouteException)
        {
            return null;
        }
        _pc = route;
        _helm.Usage.RecordAgent(AiFeatureIds.AskHelm, result.Agent ?? "agent", model.Model, route.Device, result.SavedUsd);

        // Undo goes back to the PC that made the record: it may not have synced here yet.
        var done = result.Done.Select(a => new AssistantAction(a.Tool, a.Summary, c => route.UndoAsync(a, c))).ToList();
        var notes = new List<string>();
        // What only the phone can do comes back to be done here.
        var byName = _all!.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var action in result.PhoneActions)
        {
            if (!byName.TryGetValue(action.Tool, out var tool))
            {
                notes.Add($"Máy này không làm được {action.Tool}.");
                continue;
            }
            try
            {
                await tool.Source.Run(JsonDocument.Parse(action.ArgumentsJson).RootElement.Clone(), ct);
                if (!tool.Source.ReadOnly) done.Add(new AssistantAction(action.Tool, action.Summary, null));
            }
            catch (Exception ex) when (ex is McpToolException or JsonException)
            {
                notes.Add(ex.Message);
            }
        }
        var reply = string.Join(" ", new[] { result.Text }.Concat(notes).Where(t => t.Length > 0));
        return new AssistantReply(reply, done, false, null, result.Agent is { } agent ? $"{agent} trên {route.Device}" : route.Device);
    }

    private async Task<AssistantReply> OnApiAsync(string text, bool first, Func<IReadOnlyList<PendingAction>, Task<bool>> confirm, CancellationToken ct)
    {
        if (_helm.OverCap() is { } cap) return new AssistantReply("", [], false, cap);
        var moved = _open is null && _history.Count > 0;
        try
        {
            _open ??= _helm.Open(_tools);
        }
        catch (AssistantException ex)
        {
            return new AssistantReply("", [], false, ex.Message);
        }
        var (chat, model, keyId) = _open.Value;
        // A conversation that started on a PC goes on here with what was said so far.
        var context = moved ? "Earlier in this conversation:\n" + string.Join("\n", _history.Select(h => $"{h.Role}: {h.Text}")) + "\n\n" : "";
        chat.AddUser(first || moved ? $"{_helm.Now()}\n\n{context}{text}" : text);
        var outcome = await AssistantLoop.RunAsync(chat, _tools, confirm, u => _helm.Count(model, keyId, u),
            () => _helm.Unavailable() ?? _helm.OverCap(), phoneHere: true, model.Name, ct);
        return new AssistantReply(outcome.Text, outcome.Done, outcome.Declined, outcome.Problem);
    }
}
