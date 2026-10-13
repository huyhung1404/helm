using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Modules.Workspace;

public enum PendingKind
{
    /// <summary>Claude wants to run a tool and asks to be allowed.</summary>
    Permission,
    /// <summary>Claude asks you a question with choices (AskUserQuestion).</summary>
    Question,
    /// <summary>Claude presents a plan and waits for you to approve it (ExitPlanMode in plan mode).</summary>
    Plan,
}

public sealed record QuestionOption(string Label, string Description);

public sealed record QuestionInfo
{
    public string Question { get; init; } = "";
    public string Header { get; init; } = "";
    public IReadOnlyList<QuestionOption> Options { get; init; } = [];
    public bool MultiSelect { get; init; }
}

/// <summary>
/// Something Claude waits for you to decide, held by the PC while the phone answers (Claude Code's PermissionRequest
/// hook stays open until then). Travels in the session record; the answer comes back as a command.
/// </summary>
public sealed record PendingRequest
{
    public string RequestId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public PendingKind Kind { get; init; }
    public string Tool { get; init; } = "";

    /// <summary>What exactly would run: the command, the file, the address (shown as is, shortened).</summary>
    public string Detail { get; init; } = "";

    public IReadOnlyList<QuestionInfo> Questions { get; init; } = [];

    /// <summary>A plan: its Markdown.</summary>
    public string? Plan { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>After this the PC stops waiting and VS Code shows its own dialog.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public const int MaxDetail = 1500;

    /// <summary>A plan longer than this is cut (it travels in the session record).</summary>
    public const int MaxPlan = 14_000;

    /// <summary>The request for a PermissionRequest hook (AskUserQuestion becomes a question).</summary>
    public static PendingRequest From(ClaudeHookEvent e, string requestId, DateTimeOffset now, TimeSpan hold)
    {
        var tool = e.ToolName ?? "";
        var input = ParseObject(e.ToolInputJson);
        var questions = tool == "AskUserQuestion" ? ReadQuestions(input) : [];
        var plan = tool == "ExitPlanMode" && input?["plan"] is JsonValue p && p.GetValueKind() == JsonValueKind.String ? p.GetValue<string>() : null;
        return new PendingRequest
        {
            RequestId = requestId,
            SessionId = e.SessionId,
            Kind = plan is not null ? PendingKind.Plan : questions.Count > 0 ? PendingKind.Question : PendingKind.Permission,
            Tool = tool,
            Detail = plan is not null ? "" : Summarize(tool, input),
            Questions = questions,
            Plan = plan is null ? null : plan.Length <= MaxPlan ? plan : plan[..(MaxPlan - 1)] + "…",
            CreatedAt = now,
            ExpiresAt = now + hold,
        };
    }

    /// <summary>The words the phone shows for what would run.</summary>
    internal static string Summarize(string tool, JsonObject? input)
    {
        if (input is null) return "";
        string? Text(string name) => input[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        var text = tool switch
        {
            "Bash" or "PowerShell" => Text("command") is { } c ? c + (Text("description") is { } d ? $"\n({d})" : "") : null,
            "Edit" or "Write" or "Read" or "NotebookEdit" or "MultiEdit" => Text("file_path") ?? Text("notebook_path"),
            "WebFetch" => Text("url"),
            "WebSearch" => Text("query"),
            "AskUserQuestion" => null,
            _ => null,
        } ?? input.ToJsonString();
        return text.Length <= MaxDetail ? text : text[..(MaxDetail - 1)] + "…";
    }

    private static IReadOnlyList<QuestionInfo> ReadQuestions(JsonObject? input)
    {
        if (input?["questions"] is not JsonArray list) return [];
        var questions = new List<QuestionInfo>();
        foreach (var q in list.OfType<JsonObject>())
        {
            var options = (q["options"] as JsonArray)?.OfType<JsonObject>()
                .Select(o => new QuestionOption(Str(o, "label"), Str(o, "description")))
                .Where(o => o.Label.Length > 0).ToList() ?? [];
            questions.Add(new QuestionInfo
            {
                Question = Str(q, "question"),
                Header = Str(q, "header"),
                Options = options,
                MultiSelect = q["multiSelect"] is JsonValue m && m.GetValueKind() == JsonValueKind.True,
            });
        }
        return questions.Where(q => q.Question.Length > 0).ToList();
    }

    private static string Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The PermissionRequest hook answer: allow, or deny with a message Claude reads.</summary>
    public static string Decision(bool allow, string? message = null)
    {
        var decision = new JsonObject { ["behavior"] = allow ? "allow" : "deny" };
        if (!allow) decision["message"] = message is { Length: > 0 } m ? m : "Denied from Helm on the phone.";
        return Wrap(decision);
    }

    /// <summary>
    /// The answer to an AskUserQuestion: allowed, with the answers filled into the tool's input (question text → answer;
    /// several choices comma-separated), so Claude gets them as if you had picked them in VS Code.
    /// </summary>
    public static string Answer(string? toolInputJson, IReadOnlyDictionary<string, string> answers)
    {
        var input = ParseObject(toolInputJson) ?? new JsonObject();
        var map = new JsonObject();
        foreach (var (question, answer) in answers) map[question] = answer;
        input["answers"] = map;
        return Wrap(new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input });
    }

    /// <summary>
    /// The answer to a plan: approved, and the session goes on in <paramref name="mode"/> (acceptEdits, default, auto),
    /// as VS Code's own "Yes, …" choices do; or, with a null mode, not approved: Claude keeps planning and reads
    /// <paramref name="feedback"/>.
    /// </summary>
    public static string PlanDecision(string? mode, string? feedback)
    {
        if (mode is null)
            return Wrap(new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = feedback is { Length: > 0 } f ? "Keep planning. " + f : "Keep planning: the plan was not approved yet.",
            });
        return Wrap(new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedPermissions"] = new JsonArray(new JsonObject
            {
                ["type"] = "setMode",
                ["mode"] = PermissionModes.Normalize(mode),
                ["destination"] = "session",
            }),
        });
    }

    private static string Wrap(JsonObject decision) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = ClaudeHookEvent.PermissionRequest,
            ["decision"] = decision,
        },
    }.ToJsonString();
}

/// <summary>When the PC holds Claude's permission requests for the phone.</summary>
public enum PhoneApprovals
{
    /// <summary>Never: VS Code asks as usual.</summary>
    Off,
    /// <summary>Only while nobody uses this PC (no keyboard or mouse for a while); VS Code asks otherwise.</summary>
    WhenAway,
    /// <summary>Always ask the phone first (VS Code asks after the phone did not answer in time).</summary>
    Always,
}
