using System.Text.RegularExpressions;

namespace Helm.Modules.SlackBot;

/// <summary>
/// The user switching Lumi's model or effort from Slack ("@lumi model fable", "@lumi dùng opus effort cao",
/// "@lumi model mặc định"), or asking which ones it uses ("@lumi đang dùng model gì?"). Only the user's own messages
/// are read this way; for anyone else these are words like any other.
/// </summary>
public sealed partial record SlackAiCommand(string? Model, string? Effort, bool Ask)
{
    /// <summary>The command in <paramref name="text"/>, or null when it asks for none ("" = back to the default).</summary>
    public static SlackAiCommand? Parse(string text)
    {
        var model = ModelWord().Match(text) is { Success: true } m ? ToModel(m.Groups["v"].Value) : null;
        var effort = EffortWord().Match(text) is { Success: true } e ? ToEffort(e.Groups["v"].Value) : null;
        if (model is not null || effort is not null) return new SlackAiCommand(model, effort, false);
        return AskWord().IsMatch(text) ? new SlackAiCommand(null, null, true) : null;
    }

    /// <summary>The conversation's choice after this command.</summary>
    public SlackAiChoice Apply(SlackAiChoice? current) =>
        new() { Model = Model ?? current?.Model ?? "", Effort = Effort ?? current?.Effort ?? "" };

    /// <summary>What Lumi says back, in the user's language (Vietnamese).</summary>
    public static string Describe((string? Model, string? Effort) ai) =>
        $"model {SlackAiOptions.Short(ai.Model ?? "", "mặc định của Claude Code")}, effort {SlackAiOptions.Short(ai.Effort ?? "", "mặc định")}";

    private static string ToModel(string word) => word.ToLowerInvariant() switch
    {
        "default" or "mặc định" => "",
        var w => w,
    };

    private static string ToEffort(string word) => word.ToLowerInvariant() switch
    {
        "default" or "mặc định" => "",
        "thấp" => "low",
        "trung bình" or "vừa" => "medium",
        "cao" => "high",
        "rất cao" or "extra high" or "x-high" => "xhigh",
        "tối đa" => "max",
        var w => w,
    };

    // "model fable", "/model: opus", "dùng sonnet", "đổi sang haiku", "use opus", "switch to fable".
    [GeneratedRegex(@"(?:/?model|mô hình|dùng|xài|đổi sang|chuyển sang|use|switch to)\s*[:=]?\s*(?<v>opus|fable|sonnet|haiku|default|mặc định)(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase)]
    private static partial Regex ModelWord();

    // "effort high", "effort: rất cao", "mức tối đa".
    [GeneratedRegex(@"(?:/?effort|mức)\s*[:=]?\s*(?<v>low|medium|high|xhigh|x-high|extra high|max|default|mặc định|rất cao|thấp|trung bình|vừa|cao|tối đa)(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase)]
    private static partial Regex EffortWord();

    // "đang dùng model gì?", "model nào", "which model", "what model".
    [GeneratedRegex(@"(?:model|effort)\s*(?:gì|nào|\?)|(?:which|what)\s+(?:model|effort)", RegexOptions.IgnoreCase)]
    private static partial Regex AskWord();
}
