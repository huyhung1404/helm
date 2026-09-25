using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>A wider "allow" the user can pick instead of allowing just this one call.</summary>
/// <param name="Label">Says exactly what is allowed, e.g. "Allow <c>mkdir alpha *</c> for this chat".</param>
/// <param name="UpdatedPermissions">The array sent back as <c>updatedPermissions</c>; always scoped to the session.</param>
public sealed record PermissionOption(string Label, JsonElement UpdatedPermissions);

public static class PermissionOptions
{
    /// <summary>
    /// Turns the CLI's <c>permission_suggestions</c> into options. Every option is forced to
    /// <c>destination: "session"</c>: the CLI suggests <c>localSettings</c> for command rules, which would write the
    /// rule into the project's .claude/settings.local.json for good — never what "for this chat" means. Suggestions
    /// Helm does not understand, and any switch to bypassPermissions, are dropped.
    /// </summary>
    public static IReadOnlyList<PermissionOption> From(JsonElement? suggestions)
    {
        if (suggestions is not { ValueKind: JsonValueKind.Array } array) return [];
        var options = new List<PermissionOption>();
        foreach (var suggestion in array.EnumerateArray())
        {
            if (suggestion.ValueKind != JsonValueKind.Object) continue;
            var label = Describe(suggestion);
            if (label is null) continue;

            var scoped = JsonNode.Parse(suggestion.GetRawText())!.AsObject();
            scoped["destination"] = "session";
            options.Add(new PermissionOption(label, ToElement(new JsonArray(scoped))));
        }
        return options;
    }

    private static string? Describe(JsonElement suggestion)
    {
        switch (Str(suggestion, "type"))
        {
            case "addRules" when Str(suggestion, "behavior") is null or "allow":
                if (!suggestion.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return null;
                var parts = rules.EnumerateArray()
                    .Select(r => Str(r, "ruleContent") is { Length: > 0 } content ? content : $"every {Str(r, "toolName")} call")
                    .ToList();
                return parts.Count == 0 ? null : $"Allow {string.Join(", ", parts)} for this chat";

            case "addDirectories":
                if (!suggestion.TryGetProperty("directories", out var dirs) || dirs.ValueKind != JsonValueKind.Array) return null;
                var list = dirs.EnumerateArray().Select(d => d.GetString()).Where(d => !string.IsNullOrEmpty(d)).ToList();
                return list.Count == 0 ? null : $"Allow access to {string.Join(", ", list)} for this chat";

            case "setMode":
                return Str(suggestion, "mode") switch
                {
                    "acceptEdits" => "Allow all file edits for this chat",
                    "plan" => "Switch this chat to plan mode",
                    _ => null, // bypassPermissions and anything new: not offered
                };

            default:
                return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement ToElement(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}

public enum PermissionState
{
    Pending,
    AllowedOnce,
    AllowedWider,
    Denied,

    /// <summary>The turn ended (Stop, crash, new chat) before the user answered.</summary>
    Expired,
}

/// <summary>An inline "Claude wants to…" card. The answer goes back through the callback given by the chat.</summary>
public sealed partial class PermissionChatItem : ChatItem
{
    internal const string DeniedMessage = "The user denied this in Helm.";

    private readonly Func<PermissionChatItem, PermissionOption?, bool, Task> _answer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(Outcome))]
    [NotifyCanExecuteChangedFor(nameof(AllowOnceCommand), nameof(AllowWithCommand), nameof(DenyCommand))]
    private PermissionState _state = PermissionState.Pending;

    private string? _chosenLabel;

    public PermissionChatItem(PermissionRequest request, Func<PermissionChatItem, PermissionOption?, bool, Task> answer)
    {
        Request = request;
        _answer = answer;
        Summary = ToolChatItem.Summarize(request.ToolName, request.Input);
        Details = FormatInput(request.Input);
        Options = PermissionOptions.From(request.Suggestions);
    }

    public PermissionRequest Request { get; }

    public string Title => $"Claude wants to use {Request.DisplayName}";

    public string Summary { get; }

    /// <summary>The full tool input, indented, for the expander.</summary>
    public string Details { get; }

    /// <summary>Why the CLI asks (e.g. "Path is outside allowed working directories"); may be null.</summary>
    public string? Reason => Request.DecisionReason;

    public IReadOnlyList<PermissionOption> Options { get; }

    public bool IsPending => State == PermissionState.Pending;

    public string? Outcome => State switch
    {
        PermissionState.AllowedOnce => "Allowed once",
        PermissionState.AllowedWider => _chosenLabel,
        PermissionState.Denied => "Denied",
        PermissionState.Expired => "Not answered — the turn ended",
        _ => null,
    };

    [RelayCommand(CanExecute = nameof(IsPending))]
    private Task AllowOnceAsync() => AnswerAsync(null, allow: true);

    [RelayCommand(CanExecute = nameof(IsPending))]
    private Task AllowWithAsync(PermissionOption? option) => option is null ? Task.CompletedTask : AnswerAsync(option, allow: true);

    [RelayCommand(CanExecute = nameof(IsPending))]
    private Task DenyAsync() => AnswerAsync(null, allow: false);

    /// <summary>Called by the chat when the request can no longer be answered.</summary>
    internal void Expire()
    {
        if (IsPending) State = PermissionState.Expired;
    }

    private async Task AnswerAsync(PermissionOption? option, bool allow)
    {
        if (!IsPending) return;
        _chosenLabel = option?.Label;
        State = !allow ? PermissionState.Denied : option is null ? PermissionState.AllowedOnce : PermissionState.AllowedWider;
        await _answer(this, option, allow);
    }

    private static string FormatInput(JsonElement input)
    {
        try
        {
            return JsonSerializer.Serialize(input, s_indented);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return input.GetRawText();
        }
    }

    private static readonly JsonSerializerOptions s_indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
