using Microsoft.Extensions.Logging;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Answers for the user where they allowed it (<see cref="SlackChannelMode.Reply"/>): a message for them that Claude
/// put on the to-do list and that they have not answered within a few minutes gets the reply template, which says it
/// comes from an AI assistant, so a wrong reply is never taken for the user's own. Claude never writes the reply (a
/// message cannot make it say something else), only once in a while per DM or thread, never hours later, and the item
/// stays on the user's list until they answer it themselves.
/// </summary>
public sealed class SlackReplier(ISlackApi api, SlackInbox inbox, SlackReader reader, Func<SlackBotSettings> settings, string me, ILogger logger)
{
    /// <summary>No reply to a message older than this (after the PC was off, say).</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    /// <summary>At most one reply per DM or thread in this time.</summary>
    internal static readonly TimeSpan PerScope = TimeSpan.FromHours(4);

    /// <summary>The template with the user's name, always opening with 🤖 so nobody takes it for the user.</summary>
    internal static string Text(string template, string me)
    {
        var text = template.Replace("{me}", me, StringComparison.Ordinal).Trim();
        return text.StartsWith("🤖", StringComparison.Ordinal) ? text : "🤖 " + text;
    }

    /// <summary>Sends the replies that are due; how many went.</summary>
    public async Task<int> ReplyAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = settings();
        if (string.IsNullOrWhiteSpace(s.ReplyTemplate)) return 0;
        var text = Text(s.ReplyTemplate, me);
        var sent = 0;
        foreach (var item in inbox.ToReply(now, TimeSpan.FromMinutes(Math.Max(1, s.ReplyAfterMinutes)), MaxAge, PerScope))
        {
            if (reader.ModeOf(item.Channel) != SlackChannelMode.Reply) continue;
            // In a channel the reply goes in the message's thread, so the channel is not flooded.
            var thread = item.Kind is SlackConversationKind.DirectMessage or SlackConversationKind.GroupMessage ? item.ThreadTs : item.ThreadTs ?? item.Ts;
            inbox.Sending(item.Channel);
            try
            {
                var ts = await api.PostMessageAsync(item.Channel, text, thread, ct).ConfigureAwait(false);
                inbox.BotReplied(item.Id, item.Channel, ts, text);
                sent++;
            }
            catch (SlackApiException ex)
            {
                inbox.NotSent(item.Channel);
                logger.LogWarning(ex, "Slack Bot could not reply in {Channel}", item.Channel);
                if (ex.IsAuthProblem) throw;
            }
            catch
            {
                inbox.NotSent(item.Channel);
                throw;
            }
        }
        return sent;
    }
}
