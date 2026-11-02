using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Helm.Modules.SlackBot;

/// <summary>Slack said no: <see cref="Error"/> is its error code (invalid_auth, missing_scope, not_in_channel…).</summary>
public sealed class SlackApiException(string method, string error)
    : Exception($"Slack refused {method}: {error}.")
{
    public string Error { get; } = error;

    /// <summary>The token is wrong, revoked or lacks a permission: trying again will not help.</summary>
    public bool IsAuthProblem => Error is "invalid_auth" or "not_authed" or "token_revoked" or "account_inactive" or "missing_scope"
        or "token_expired" or "no_permission";
}

/// <summary>The few Slack Web API calls SlackBot makes, all reading as the user (a fake in tests).</summary>
public interface ISlackApi
{
    Task<SlackIdentity> AuthTestAsync(CancellationToken ct);

    /// <summary>Every channel, private channel, DM and group DM the user is in (not archived).</summary>
    Task<IReadOnlyList<SlackConversation>> ConversationsAsync(CancellationToken ct);

    /// <summary>Messages of <paramref name="channel"/> after <paramref name="oldest"/>, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<SlackMessage>> HistoryAsync(string channel, string oldest, int max, CancellationToken ct);

    /// <summary>Replies in a thread after <paramref name="oldest"/>, oldest first.</summary>
    Task<IReadOnlyList<SlackMessage>> RepliesAsync(string channel, string threadTs, string oldest, CancellationToken ct);

    /// <summary>Messages anywhere that call <paramref name="userId"/>, after <paramref name="oldest"/> (search; replies included).</summary>
    Task<IReadOnlyList<SlackMessage>> SearchMentionsAsync(string userId, string oldest, CancellationToken ct);

    /// <summary>A person's display name (remembered), or null when Slack does not know them.</summary>
    Task<string?> UserNameAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Writes as the user (in a thread when given): <paramref name="blocks"/> (Block Kit JSON) when given, with
    /// <paramref name="text"/> for notifications; the new message's ts.
    /// </summary>
    Task<string?> PostMessageAsync(string channel, string text, string? blocks, string? threadTs, CancellationToken ct);

    /// <summary>A fresh Socket Mode address (apps.connections.open, with the app-level token).</summary>
    Task<Uri> OpenSocketAsync(CancellationToken ct);
}

/// <summary>
/// The Web API over HTTPS with the user token (xoxp-) and, for Socket Mode only, the app-level token (xapp-). A "slow
/// down" (429) waits as long as Slack asks and tries again; history calls are spaced to stay inside Slack's limits.
/// </summary>
public sealed class SlackApi : ISlackApi, IDisposable
{
    private const string BaseUrl = "https://slack.com/api/";

    /// <summary>conversations.history and .replies allow about 50 calls a minute.</summary>
    private static readonly TimeSpan HistorySpacing = TimeSpan.FromMilliseconds(1300);

    private readonly HttpClient _http;
    private readonly string _userToken;
    private readonly string _appToken;
    private readonly Dictionary<string, string?> _names = [];
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private DateTime _lastHistoryCall = DateTime.MinValue;

    public SlackApi(string userToken, string appToken, HttpMessageHandler? handler = null)
    {
        _userToken = userToken;
        _appToken = appToken;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public void Dispose()
    {
        _http.Dispose();
        _historyGate.Dispose();
    }

    public async Task<SlackIdentity> AuthTestAsync(CancellationToken ct)
    {
        using var json = await CallAsync("auth.test", [], _userToken, ct).ConfigureAwait(false);
        var r = json.RootElement;
        return new SlackIdentity(Str(r, "user_id") ?? "", Str(r, "user") ?? "", Str(r, "team_id") ?? "", Str(r, "url") ?? "https://slack.com/");
    }

    public async Task<IReadOnlyList<SlackConversation>> ConversationsAsync(CancellationToken ct)
    {
        var list = new List<SlackConversation>();
        string? cursor = null;
        do
        {
            using var json = await CallAsync("users.conversations",
                [("types", "public_channel,private_channel,mpim,im"), ("exclude_archived", "true"), ("limit", "200"), ("cursor", cursor)],
                _userToken, ct).ConfigureAwait(false);
            foreach (var c in Array(json.RootElement, "channels"))
            {
                if (Str(c, "id") is not { } id) continue;
                if (Bool(c, "is_im"))
                    list.Add(new SlackConversation(id, "", SlackConversationKind.DirectMessage, Str(c, "user"), Bool(c, "is_user_deleted")));
                else if (Bool(c, "is_mpim"))
                    list.Add(new SlackConversation(id, GroupName(Str(c, "name") ?? id), SlackConversationKind.GroupMessage));
                else
                    list.Add(new SlackConversation(id, Str(c, "name") ?? id, Bool(c, "is_private") ? SlackConversationKind.PrivateChannel : SlackConversationKind.Channel));
            }
            cursor = NextCursor(json.RootElement);
        }
        while (cursor is not null);
        return list;
    }

    public async Task<IReadOnlyList<SlackMessage>> HistoryAsync(string channel, string oldest, int max, CancellationToken ct)
    {
        var list = new List<SlackMessage>();
        string? cursor = null;
        do
        {
            await SpaceHistoryAsync(ct).ConfigureAwait(false);
            using var json = await CallAsync("conversations.history",
                [("channel", channel), ("oldest", oldest), ("limit", "100"), ("cursor", cursor)], _userToken, ct).ConfigureAwait(false);
            foreach (var m in Array(json.RootElement, "messages"))
                if (SlackEventParser.Message(m, channel) is { } message) list.Add(message);
            cursor = NextCursor(json.RootElement);
        }
        while (cursor is not null && list.Count < max);
        list.Sort((a, b) => SlackIds.Compare(a.Ts, b.Ts));
        return list.Count > max ? list[^max..] : list;
    }

    public async Task<IReadOnlyList<SlackMessage>> RepliesAsync(string channel, string threadTs, string oldest, CancellationToken ct)
    {
        await SpaceHistoryAsync(ct).ConfigureAwait(false);
        using var json = await CallAsync("conversations.replies",
            [("channel", channel), ("ts", threadTs), ("oldest", oldest), ("limit", "200")], _userToken, ct).ConfigureAwait(false);
        var list = new List<SlackMessage>();
        foreach (var m in Array(json.RootElement, "messages"))
        {
            // The parent always comes first, whatever "oldest" says.
            if (SlackEventParser.Message(m, channel) is { } message && SlackIds.Compare(message.Ts, oldest) > 0) list.Add(message);
        }
        return list;
    }

    public async Task<IReadOnlyList<SlackMessage>> SearchMentionsAsync(string userId, string oldest, CancellationToken ct)
    {
        using var json = await CallAsync("search.messages",
            [("query", $"<@{userId}>"), ("sort", "timestamp"), ("sort_dir", "desc"), ("count", "100")], _userToken, ct).ConfigureAwait(false);
        var list = new List<SlackMessage>();
        if (!json.RootElement.TryGetProperty("messages", out var messages)) return list;
        foreach (var m in Array(messages, "matches"))
        {
            if (!m.TryGetProperty("channel", out var c) || Str(c, "id") is not { } channel) continue;
            if (SlackEventParser.Message(m, channel) is not { } message || SlackIds.Compare(message.Ts, oldest) <= 0) continue;
            // Search does not say the thread; the link does.
            list.Add(message.ThreadTs is null && ThreadFromLink(Str(m, "permalink")) is { } thread ? message with { ThreadTs = thread } : message);
        }
        list.Sort((a, b) => SlackIds.Compare(a.Ts, b.Ts));
        return list;
    }

    public async Task<string?> UserNameAsync(string userId, CancellationToken ct)
    {
        lock (_names)
        {
            if (_names.TryGetValue(userId, out var known)) return known;
        }
        string? name;
        try
        {
            using var json = await CallAsync("users.info", [("user", userId)], _userToken, ct).ConfigureAwait(false);
            name = json.RootElement.TryGetProperty("user", out var u) ? DisplayName(u) : null;
        }
        catch (SlackApiException ex) when (ex.Error is "user_not_found" or "user_not_visible")
        {
            name = null;
        }
        lock (_names) _names[userId] = name;
        return name;
    }

    public async Task<string?> PostMessageAsync(string channel, string text, string? blocks, string? threadTs, CancellationToken ct)
    {
        using var json = await CallAsync("chat.postMessage",
            [("channel", channel), ("text", text), ("blocks", blocks), ("thread_ts", threadTs), ("unfurl_links", "false"), ("unfurl_media", "false")],
            _userToken, ct).ConfigureAwait(false);
        return Str(json.RootElement, "ts");
    }

    public async Task<Uri> OpenSocketAsync(CancellationToken ct)
    {
        using var json = await CallAsync("apps.connections.open", [], _appToken, ct).ConfigureAwait(false);
        return Str(json.RootElement, "url") is { } url ? new Uri(url) : throw new SlackApiException("apps.connections.open", "no_url");
    }

    /// <summary>Display name, else full name, else the account name.</summary>
    internal static string? DisplayName(JsonElement user)
    {
        if (user.TryGetProperty("profile", out var p))
        {
            if (Str(p, "display_name") is { Length: > 0 } display) return display;
            if (Str(p, "real_name") is { Length: > 0 } real) return real;
        }
        return Str(user, "real_name") is { Length: > 0 } r ? r : Str(user, "name");
    }

    /// <summary>"mpdm-an--binh--chi-1" → "an, binh, chi".</summary>
    internal static string GroupName(string name)
    {
        var s = name.StartsWith("mpdm-", StringComparison.Ordinal) ? name[5..] : name;
        var dash = s.LastIndexOf('-');
        if (dash > 0 && s[(dash + 1)..].All(char.IsDigit)) s = s[..dash];
        return string.Join(", ", s.Split("--", StringSplitOptions.RemoveEmptyEntries));
    }

    internal static string? ThreadFromLink(string? permalink)
    {
        if (permalink is null) return null;
        var at = permalink.IndexOf("thread_ts=", StringComparison.Ordinal);
        if (at < 0) return null;
        var value = permalink[(at + 10)..];
        var end = value.IndexOf('&');
        return end < 0 ? value : value[..end];
    }

    private async Task SpaceHistoryAsync(CancellationToken ct)
    {
        await _historyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastHistoryCall + HistorySpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastHistoryCall = DateTime.UtcNow;
        }
        finally
        {
            _historyGate.Release();
        }
    }

    private async Task<JsonDocument> CallAsync(string method, IReadOnlyList<(string Name, string? Value)> args, string token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + method)
            {
                Content = new FormUrlEncodedContent(args.Where(a => a.Value is not null).Select(a => new KeyValuePair<string, string>(a.Name, a.Value!))),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                await Task.Delay(wait + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return json;
            var error = Str(json.RootElement, "error") ?? "unknown_error";
            json.Dispose();
            throw new SlackApiException(method, error);
        }
    }

    private static string? NextCursor(JsonElement r) =>
        r.TryGetProperty("response_metadata", out var meta) && Str(meta, "next_cursor") is { Length: > 0 } next ? next : null;

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
