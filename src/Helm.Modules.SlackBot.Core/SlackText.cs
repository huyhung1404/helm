using System.Net;
using System.Text.RegularExpressions;

namespace Helm.Modules.SlackBot;

/// <summary>Slack's message markup: who a message calls, its plain text, and links back into Slack.</summary>
public static partial class SlackText
{
    /// <summary>The message calls the user by name (&lt;@U123&gt; or &lt;@U123|name&gt;).</summary>
    public static bool Mentions(string text, string userId) =>
        text.Contains("<@" + userId + ">", StringComparison.Ordinal) || text.Contains("<@" + userId + "|", StringComparison.Ordinal);

    /// <summary>The message calls everyone in the channel (@here, @channel, @everyone).</summary>
    public static bool MentionsEveryone(string text) =>
        text.Contains("<!here", StringComparison.Ordinal) || text.Contains("<!channel", StringComparison.Ordinal)
        || text.Contains("<!everyone", StringComparison.Ordinal);

    /// <summary>
    /// The text as a person reads it: &lt;@U123&gt; becomes @Name (<paramref name="userName"/>, else the name Slack put in
    /// the tag), channels #name, links their label (or the address), and &amp;amp; &amp;lt; &amp;gt; their characters.
    /// </summary>
    public static string Plain(string text, Func<string, string?> userName)
    {
        var plain = Tag().Replace(text, m =>
        {
            var body = m.Groups[1].Value;
            var bar = body.IndexOf('|');
            var target = bar < 0 ? body : body[..bar];
            var label = bar < 0 ? null : body[(bar + 1)..];
            if (target.StartsWith('@')) return "@" + (userName(target[1..]) ?? label ?? target[1..]);
            if (target.StartsWith('#')) return "#" + (label ?? target[1..]);
            if (target.StartsWith("!subteam^", StringComparison.Ordinal)) return label ?? "@group";
            if (target.StartsWith('!')) return "@" + (label ?? target[1..]);
            return label is { Length: > 0 } ? $"{label} ({target})" : target;
        });
        return WebUtility.HtmlDecode(plain);
    }

    /// <summary>At most <paramref name="max"/> characters, cut at a word, with … when cut.</summary>
    public static string Clip(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The message's address in the workspace (<paramref name="workspaceUrl"/>, e.g. https://ikame.slack.com/), which
    /// opens it in the Slack app. A reply also names its thread.
    /// </summary>
    public static string Permalink(string workspaceUrl, string channel, string ts, string? threadTs)
    {
        var link = $"{workspaceUrl.TrimEnd('/')}/archives/{channel}/p{ts.Replace(".", "", StringComparison.Ordinal)}";
        return threadTs is { } t && t != ts ? $"{link}?thread_ts={t}&cid={channel}" : link;
    }

    [GeneratedRegex("<([^<>]+)>")]
    private static partial Regex Tag();
}
