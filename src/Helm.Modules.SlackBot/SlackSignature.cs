using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helm.Modules.SlackBot;

/// <summary>
/// A message Slack Bot posts for the user: the words, then a small grey line with the bot's emoji and its label
/// ("Lumi · trợ lý AI của Hưng"), as Slack shows a bot's credit. The plain text (notifications, old clients) carries the
/// label in front.
/// </summary>
public sealed record SlackSignedMessage(string Body, string Label, string Emoji)
{
    /// <summary>What notifications and clients without blocks show.</summary>
    public string Fallback => $"{Label}: {Body}";

    /// <summary>The Block Kit layout: the words, then the signature as a context line.</summary>
    public string Blocks => JsonSerializer.Serialize(new object[]
    {
        new { type = "section", text = new { type = "mrkdwn", text = Body } },
        new { type = "context", elements = new object[] { new { type = "mrkdwn", text = $"{Emoji} {Label}" } } },
    }, Json);

    /// <summary>How it looks, for the settings page and the question before an agent posts.</summary>
    public string Preview => $"{Body}\n\n{Emoji} {Label}";

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

public static partial class SlackSignature
{
    public const string DefaultEmoji = ":robot_face:";

    /// <summary>The label with the user's name; it always says AI, so nobody takes the message for the user's own.</summary>
    public static string Label(string template, string me)
    {
        var label = (string.IsNullOrWhiteSpace(template) ? SlackBotSettings.DefaultSignature : template).Replace("{me}", me, StringComparison.Ordinal).Trim();
        return label.Contains("AI", StringComparison.OrdinalIgnoreCase) ? label : label + " (AI)";
    }

    /// <summary>A Slack emoji code (:lumi:, :robot_face:), or the default when it is not one.</summary>
    public static string Emoji(string emoji) => EmojiCode().IsMatch(emoji.Trim()) ? emoji.Trim() : DefaultEmoji;

    /// <summary>
    /// The answer with the person it answers tagged in front (by Helm, not Claude), so they are told; nobody is tagged
    /// when it answers the user themself or a bot.
    /// </summary>
    public static string ToPerson(string body, string? userId, string meId) =>
        userId is { Length: > 0 } u && u != meId ? $"<@{u}> {body}" : body;

    public static SlackSignedMessage Sign(string body, SlackBotSettings settings, string me) =>
        new(body, Label(settings.Signature, me), Emoji(settings.SignatureEmoji));

    [GeneratedRegex("^:[a-z0-9_+'-]+:$")]
    private static partial Regex EmojiCode();
}
