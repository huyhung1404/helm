using System.Text.Json;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Reads Slack's JSON (Socket Mode events and the messages of conversations.history / .replies) into
/// <see cref="SlackEvent"/>s. Kinds SlackBot has no use for (joins, topic changes, other event types) come back null.
/// </summary>
public static class SlackEventParser
{
    /// <summary>Message subtypes that are someone saying something; every other subtype is a channel event.</summary>
    private static readonly HashSet<string> SaidSubtypes = ["thread_broadcast", "file_share", "me_message", "bot_message"];

    /// <summary>An event of an events_api envelope (its payload.event).</summary>
    public static SlackEvent? Parse(JsonElement e)
    {
        switch (Str(e, "type"))
        {
            case "message":
            {
                var channel = Str(e, "channel");
                if (channel is null) return null;
                switch (Str(e, "subtype"))
                {
                    case "message_changed":
                        return e.TryGetProperty("message", out var changed) && Str(changed, "ts") is { } ts && Str(changed, "text") is { } text
                            ? new SlackMessageEdited(channel, ts, text)
                            : null;
                    case "message_deleted":
                        return Str(e, "deleted_ts") is { } deleted ? new SlackMessageDeleted(channel, deleted) : null;
                    default:
                        return Message(e, channel) is { } message ? new SlackMessagePosted(message) : null;
                }
            }
            case "reaction_added":
                return Str(e, "user") is { } user && e.TryGetProperty("item", out var item) && Str(item, "type") == "message"
                       && Str(item, "channel") is { } c && Str(item, "ts") is { } t
                    ? new SlackReactionAdded(user, c, t)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>One message of <paramref name="channel"/>; null for channel events (joins, renames…).</summary>
    public static SlackMessage? Message(JsonElement m, string channel)
    {
        var subtype = Str(m, "subtype");
        if (subtype is not null && !SaidSubtypes.Contains(subtype)) return null;
        if (Str(m, "ts") is not { } ts) return null;
        var text = Str(m, "text") ?? "";
        // A file with no words still says something.
        if (text.Length == 0 && m.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
            text = "(sent a file: " + (Str(files[0], "name") ?? "file") + ")";
        var fromBot = subtype == "bot_message" || Str(m, "bot_id") is not null;
        return new SlackMessage(channel, ts, Str(m, "thread_ts"), Str(m, "user"), text, fromBot, Str(m, "parent_user_id"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
