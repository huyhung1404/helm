using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai.Prompts;
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
public sealed record AssistantReply(string Text, IReadOnlyList<AssistantAction> Done, bool Declined, string? Problem, string? RanOn = null)
{
    /// <summary>Why Auto passed over the PC before the API answered ("Grok: signed out"); null when nothing was.</summary>
    public string? Skipped { get; init; }
}

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
    TimeProvider? time = null,
    IAiPrompts? prompts = null)
{
    /// <summary>The tools of Helm's own tools that suit a quick spoken request (no analysis pipelines, no servers).</summary>
    public static readonly IReadOnlySet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
    {
        "wallet_add_transaction", "wallet_add_debt", "wallet_debts", "wallet_summary",
        "wallet_update_transaction",
        "wallet_split_bill",
        "wallet_debt_reminder",
        "bills_list",
        "bill_add",
        "bill_pay",
        "tracker_lists", "tracker_tasks", "tracker_add_task", "tracker_complete_task", "tracker_start_task",
        "tracker_add_tasks",
        "notes_create", "notes_search", "notes_read",
        "notes_add_important_day",
        "habits_list", "habit_report", "habit_check",
        "missions_list", "mission_get",
        "mission_create",
        "mission_complete_step",
        "mission_skip_step",
        "mission_check_item",
        "vocab_add_words", "vocab_words",
        "workspace_sessions",
        "workspace_send",
        "ai_briefs", "ai_brief_undo", "ai_auto_jobs", "ai_auto_job_set",
    };

    /// <summary>Tools the phone itself adds (alarm, calendar, clipboard…) start with this.</summary>
    public const string PhoneToolPrefix = "phone_";

    /// <summary>The built-in system prompt; the user's own from the Skill Hub (helm-ask-helm) replaces it (<see cref="IAiPrompts"/>).</summary>
    /// <remarks>Short on purpose: every call pays for it in tokens (docs/ai-keys.md, "Tokens per call").</remarks>
    internal const string Instructions = """
        You are Helm's assistant on the user's phone. Their words (Vietnamese, sometimes English) come from speech
        recognition: read small mistakes generously. Do what they ask with Helm's tools (money spent or received:
        wallet_add_transaction; who owes whom: wallet_add_debt; to-dos and reminders: tracker_add_task; notes, habits,
        words, the phone's alarms, timers and calendar), or answer from their data with the reading tools.
        - Amounts are đồng: 45k = 45000, 1tr2 = 1200000, "45 nghìn" = 45000.
        - Do rather than ask: fill sensible defaults (now, today, the category the description had before) and say what
          you did. Ask one short question only when something essential is missing, like an amount.
        - Never invent an id, category, list or person: use what the tools show (find a task's id with them first).
        - Every change takes `summary`: one short Vietnamese line with the amount, what and when, e.g.
          "Chi 45.000đ · Phở · Ăn uống · hôm nay".
        - Answer in Vietnamese, one or two short warm sentences, calling the user "Anh" and yourself "em". No markdown
          or lists: a small card shows it and may read it aloud.
        - Never ask for or repeat passwords, keys or codes.
        - A shared bill ("lẩu 1tr2 chia 4, Nam với Tú chưa trả") is one wallet_split_bill. A debt reminder:
          wallet_debt_reminder, then phone_share_text with its message.
        - A goal or exam with a date is one mission_create (start: true): ordered steps, the exam last, its date the deadline.
        - Make all of a request's changes in one reply. A reply of only changes ends the turn (Helm shows their
          summaries), so when the request also asks something, call the reading tools in that same reply.
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
            var models = AiModels.Chat(key.Value.LastCheck);
            if (models.Count == 0) models = AiModels.Fallback(provider);
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

    /// <summary>The card's model sheet: Auto's steps, each PC agent and API family with its versions, the pick, the effort.</summary>
    public AssistantPicker Picker() => AssistantPicks.Build(Choice, Router.Offers(), Models());

    /// <summary>Auto (null), one PC agent on one of its models, or one API model; synced, so the PC's AI page shows it too.</summary>
    public void Choose(AssistantPick? pick) => features.Update(s =>
    {
        var choice = s.For(AiFeatureIds.AskHelm).Copy();
        AssistantPicks.Apply(choice, pick);
        s.Features[AiFeatureIds.AskHelm] = choice;
    });

    /// <summary>The slider's level (<see cref="AiEfforts.Levels"/>); what answers takes the nearest level it has.</summary>
    public void ChooseEffort(string effort) => features.Update(s =>
    {
        var choice = s.For(AiFeatureIds.AskHelm).Copy();
        choice.Effort = effort;
        s.Features[AiFeatureIds.AskHelm] = choice;
    });

    /// <summary>
    /// The model the card shows: the one chosen, else the first the user can pick. Without any key, the chosen Claude
    /// model still runs on a Helm PC's Claude Code.
    /// </summary>
    public AssistantModelOption? Current()
    {
        // One agent picked on a PC runs the model picked for it, with or without a key.
        if (AiRouter.PickedAgent(Choice) is { } agent)
            return new AssistantModelOption(Choice.Model.StartsWith("grok-", StringComparison.Ordinal) ? AiProviders.Grok : AiProviders.Claude, Choice.Model,
                Choice.Model.Length > 0 ? $"{agent} · {AssistantPicks.Name(Choice.Model)}" : agent);
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
        if (!Choice.Enabled) return "Ask Helm is turned off: AI → Features on the PC, or General › AI features on the phone.";
        if (Current() is null)
            return "No Helm PC with an agent is online, and there is no API key: add an agent on the PC (AI → Proxy) or a Claude or Grok key (AI → Models & keys); keys sync to this phone.";
        return null;
    }

    /// <summary>Why the API may not be used now (the day's cap), or null.</summary>
    internal string? OverCap() => usage.SpentToday(AiFeatureIds.AskHelm) >= Choice.DailyCapUsd
        ? $"Today's limit for Ask Helm on the API (${Choice.DailyCapUsd.ToString("0.00", CultureInfo.InvariantCulture)}) is reached. It starts again tomorrow, or raise it on the PC."
        : null;

    /// <param name="phone">The phone's own tools for this card (alarm, calendar, clipboard), which need its screen.</param>
    /// <param name="context">What the card was opened about (a transaction to describe), or null.</param>
    public AssistantSession Start(IEnumerable<IMcpToolProvider>? phone = null, AskHelmContext? context = null) => new(this, [.. phone ?? []], context);

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

    /// <param name="pick">Another model than the card's for this chat (a photo the picked one cannot read).</param>
    internal (IAssistantChat Chat, AssistantModelOption Model, string KeyId) Open(IReadOnlyList<AssistantTool> tools, AssistantModelOption? pick = null)
    {
        var model = pick ?? Current() ?? throw new AssistantException("No model to ask.");
        var key = keys.DefaultFor(model.Provider) ?? throw new AssistantException($"No {model.Provider.Name} API key.");
        var factory = factories.First(f => f.Provider == model.Provider);
        return (factory.Create(key.Value.Secret, model.Model, Choice.Effort, SystemPrompt(), tools), model, key.Id);
    }

    /// <summary>The system prompt a call starts with: the user's own when the Skill Hub has a usable one.</summary>
    internal string SystemPrompt() => prompts?.Get(AiFeatureIds.AskHelm, Instructions) ?? Instructions;

    /// <param name="feature">Ask Helm's, or <see cref="AiFeatureIds.WebSearch"/>'s for a conversation that searches the web.</param>
    internal void Count(AssistantModelOption model, string keyId, AssistantUsage u, string feature = AiFeatureIds.AskHelm) =>
        usage.Record(feature, model.Provider, model.Model, keyId, u.Input, u.Output, u.CacheRead, u.CacheWrite, u.WebSearches);

    /// <summary>Web search's own choice (<see cref="AiFeatureIds.WebSearch"/>): off, on the API, until the user changes it.</summary>
    internal AiFeatureChoice WebChoice => AiFeatureCatalog.Effective(features.Current, AiFeatureIds.WebSearch);

    /// <summary>Why a turn cannot search the web now (its route, no Claude key, its day's cap), or null.</summary>
    internal string? WebUnavailable()
    {
        var choice = WebChoice;
        if (choice.Route == AiRoutePolicy.PcOnly)
            return "Web search runs on the API only (agents on a PC do not search the web): set its route to API in AI → Features.";
        if (!HasKeyFor(AiProviders.Claude))
            return "Web search needs a Claude API key (AI → Models & keys on the PC); agents on a PC do not search the web.";
        return usage.SpentToday(AiFeatureIds.WebSearch) >= choice.DailyCapUsd
            ? $"Today's limit for web search (${choice.DailyCapUsd.ToString("0.00", CultureInfo.InvariantCulture)}) is reached. It starts again tomorrow, or raise it in AI → Features."
            : null;
    }

    /// <summary>The Claude model that searches: web search's own when it is a Claude one, else Haiku (a Grok pick has no web search here).</summary>
    internal AssistantModelOption WebModel()
    {
        var choice = WebChoice;
        var id = choice.Provider == AiProviders.Claude.Id ? choice.Model : ClaudeModels.Haiku;
        return Models().FirstOrDefault(m => m.Provider == AiProviders.Claude && m.Model == id)
            ?? new AssistantModelOption(AiProviders.Claude, id, AiModels.ShortName(id));
    }

    internal string Now()
    {
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), TimeZoneInfo.Local);
        var offset = local.Offset;
        return $"Now: {local.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, the user's local time (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}).";
    }

    internal static string Serialize(object? result) => result as string ?? JsonSerializer.Serialize(result, Output);
}

/// <summary>
/// One conversation in the card: the user's turns and follow-ups. It goes to Claude Code on a Helm PC when the
/// feature's route allows and one answers in time (and stays there), else to the API on the picked model.
/// </summary>
public sealed partial class AssistantSession
{
    private readonly HelmAssistant _helm;
    private readonly IReadOnlyList<IMcpToolProvider> _phone;
    private readonly AskHelmContext? _context;
    private readonly string _conversationId = Guid.NewGuid().ToString("N");
    private readonly List<(string Role, string Text)> _history = [];
    private IReadOnlyList<AssistantTool>? _all;
    private readonly HashSet<string> _scope = new(StringComparer.Ordinal);
    private IReadOnlyList<AssistantTool> _tools = [];
    private (IAssistantChat Chat, AssistantModelOption Model, string KeyId)? _open;
    private IAgentRoute? _pc;

    internal AssistantSession(HelmAssistant helm, IReadOnlyList<IMcpToolProvider> phone, AskHelmContext? context = null)
    {
        _helm = helm;
        _phone = phone;
        _context = context;
        // The groups the context needs, whatever the first words name.
        if (context is not null) _scope.UnionWith(context.Groups);
    }

    /// <summary>The model this conversation talks to (a new pick in the card starts a new conversation).</summary>
    public AssistantModelOption? Model => _open?.Model ?? _helm.Current();

    /// <summary>The conversation is over: a Claude Code process kept for it may stop.</summary>
    public void End() => (_pc ?? _warmed)?.End(_conversationId);

    /// <summary>
    /// The answer's text so far while the API's model writes it (Claude streams it), for a card that shows it at once;
    /// "" again when that text came before tool calls. Called on the turn's thread. Agents on a PC answer whole.
    /// </summary>
    public Action<string>? OnPartialText { get; set; }

    private IAgentRoute? _warmed;

    /// <summary>
    /// The card opened: the PC agent the first turn would go to gets ready now (its process started, sync's live
    /// channel open) while the user still speaks. Nothing when the route is the API only, or no agent is online.
    /// </summary>
    public void Warm()
    {
        if (_warmed is not null || _history.Count > 0 || _helm.Unavailable() is not null || Model is not { } model) return;
        var choice = _helm.Choice;
        if (_helm.Router.Agents(choice) is not [var route, ..]) return;
        _all ??= _helm.OfferedTools(_phone);
        // Only what a context already names; the first words bring the rest.
        var tools = _all.Where(t => _scope.Contains(ToolScope.GroupOf(t.Name))).ToList();
        static bool IsPhone(AssistantTool t) => t.Name.StartsWith(HelmAssistant.PhoneToolPrefix, StringComparison.Ordinal);
        _warmed = route;
        route.Warm(new AiJob(AiFeatureIds.AskHelm, _conversationId, "", [.. tools.Where(t => !IsPhone(t)).Select(t => t.Name)],
            [.. tools.Where(IsPhone).Select(t => t.Name)], model.Model, choice.Effort) { Agent = AiRouter.PickedAgent(choice) });
    }

    /// <param name="confirm">Asked once per step for the calls that wait for a yes; false: none of them runs.</param>
    public async Task<AssistantReply> SendAsync(string text, Func<IReadOnlyList<PendingAction>, Task<bool>> confirm, CancellationToken ct)
    {
        if (_helm.Unavailable() is { } problem) return new AssistantReply("", [], false, problem);
        var first = _history.Count == 0;
        // Weather, rates, gold, news, a post to check: the web answers it, when the user turned web search on.
        var factCheck = WebSearch.IsFactCheck(text);
        var web = _helm.WebChoice.Enabled && WebSearch.Asks(text);
        if (factCheck && !web) return new AssistantReply("", [], false, WebSearch.Off);
        _all ??= _helm.OfferedTools(_phone);
        // Only the tools this turn's request needs (and those of earlier turns): each schema costs tokens on every call.
        // A question for the web that names none of Helm's tools needs none of them (a post to check is not a request).
        if (!web || (!factCheck && ToolScope.GroupsFor(text).Count > 0))
        {
            var before = _tools;
            _tools = ToolScope.Pick(_all, _scope, text, known: first && _context is not null);
            if (_open is { } open && _tools.Where(t => before.All(b => b.Name != t.Name)).ToList() is { Count: > 0 } added) open.Chat.AddTools(added);
        }
        // The first turn of a card opened about something says what it is about.
        if (first && _context is { } context) text = context.Before(text);
        if (web)
        {
            var onWeb = await OnWebAsync(text, first, factCheck, confirm, ct);
            _history.Add(("user", text));
            _history.Add(("assistant", onWeb.Text));
            return onWeb;
        }
        var model = Model!;
        var choice = _helm.Choice;

        // An agent on a Helm PC first (free), unless the conversation already moved to the API.
        string? skipped = null;
        if (_open is null)
        {
            IReadOnlyList<IAgentRoute> routes = _pc is not null ? [_pc] : _helm.Router.Agents(choice);
            foreach (var route in routes)
            {
                if (await OnPcAsync(route, PcText(text, first), model, choice, ct, failed: why => skipped ??= why) is { } onPc)
                {
                    _history.Add(("user", text));
                    _history.Add(("assistant", onPc.Text));
                    return onPc;
                }
            }
            _pc = null;
            if (!AiRouter.MayUseApi(choice)) return new AssistantReply("", [], false, _helm.Router.NoPc(choice));
            // Auto with no agent online at all: the API answers, and the card says why no PC did.
            if (routes.Count == 0 && choice.Route == AiRoutePolicy.PreferPc && _helm.Router.Offers().Count > 0) skipped ??= _helm.Router.NoPc();
            if (!_helm.HasKeyFor(model.Provider))
                return new AssistantReply("", [], false, $"No Helm PC answered in time, and there is no {model.Provider.Name} API key to fall back on.");
        }

        var onApi = await OnApiAsync(text, first, confirm, ct);
        _history.Add(("user", text));
        _history.Add(("assistant", onApi.Text));
        return skipped is not null && onApi.Problem is null ? onApi with { Skipped = skipped } : onApi;
    }

    /// <param name="conversationId">A turn's own conversation (a photo's); null for this conversation's.</param>
    /// <param name="failed">Told why the PC did not take it, when it did not.</param>
    private async Task<AssistantReply?> OnPcAsync(IAgentRoute route, string text, AssistantModelOption model, AiFeatureChoice choice, CancellationToken ct,
        string? conversationId = null, IReadOnlyList<AssistantImage>? images = null, Action<string>? failed = null)
    {
        static bool IsPhone(AssistantTool t) => t.Name.StartsWith(HelmAssistant.PhoneToolPrefix, StringComparison.Ordinal);
        var job = new AiJob(AiFeatureIds.AskHelm, conversationId ?? _conversationId, text,
            [.. _tools.Where(t => !IsPhone(t)).Select(t => t.Name)], [.. _tools.Where(IsPhone).Select(t => t.Name)],
            model.Model, choice.Effort) { Images = images ?? [], Agent = AiRouter.PickedAgent(choice) };
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
        catch (AiRouteException ex)
        {
            failed?.Invoke(ex.Message);
            return null;
        }
        _pc = route;
        var device = result.Device ?? route.Device;
        _helm.Usage.RecordAgent(AiFeatureIds.AskHelm, result.Agent ?? "agent", model.Model, device, result.SavedUsd);

        // Undo goes back to the PC that made the record: it may not have synced here yet. A record that changed nothing
        // has nothing to take back.
        var done = result.Done.Select(a => new AssistantAction(a.Tool, a.Summary,
            McpTool.ChangedNothing(a.ResultJson) ? null : c => route.UndoAsync(a, c))).ToList();
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
        var ran = result.Agent is { } agent ? result.Model is { Length: > 0 } run ? $"{agent} · {AssistantPicks.Name(run)}" : agent : null;
        return new AssistantReply(reply, done, false, null, ran is null ? device : $"{ran} trên {device}");
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
        // With web search on, Claude may find that the request needs the web after all (WebSearch.LookupTool).
        var tools = WithLookup(chat, model);
        var outcome = await AssistantLoop.RunAsync(chat, tools, confirm, u => _helm.Count(model, keyId, u, CountedAs),
            () => _helm.Unavailable() ?? _helm.OverCap() ?? (_web ? _helm.WebUnavailable() : null), phoneHere: true, model.Name, ct,
            partial: OnPartialText);
        return new AssistantReply(outcome.Text, outcome.Done, outcome.Declined, outcome.Problem);
    }
}
