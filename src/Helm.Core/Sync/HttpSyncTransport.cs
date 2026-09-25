using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Helm.Core.Sync;

/// <summary>The token was rejected (unknown, revoked, expired) or lacks a scope. Retrying will not help.</summary>
public sealed class SyncAuthException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>Server error code: invalid_token, token_expired or insufficient_scope.</summary>
    public string Code { get; } = code;
}

/// <summary>The account's storage quota is full; free space or ask the admin for more.</summary>
public sealed class SyncQuotaException(string message) : Exception(message);

/// <summary>The server refused a request for a reason the user can act on (bad invite code, invalid name, …).</summary>
public sealed class SyncRequestException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record SyncAccountInfo(
    string AccountId, string AccountName, string TokenId, string TokenName, IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt, long UsedBytes, long QuotaBytes)
{
    public bool CanManageTokens => Scopes.Contains(SyncScopes.ManageTokens);
    public bool CanWrite => Scopes.Contains(SyncScopes.Write);
}

public sealed record SyncDeviceToken(
    string Id, string Name, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

public sealed record SyncKeyringDocument(long Version, string Data);

public static class SyncScopes
{
    public const string Read = "sync:read";
    public const string Write = "sync:write";
    public const string ManageTokens = "tokens:manage";
}

public static class SyncDefaults
{
    /// <summary>The Helm sync Worker; HELM_SYNC_SERVER overrides it (development against `wrangler dev`, or self-hosting).</summary>
    public static Uri Server { get; } =
        Environment.GetEnvironmentVariable("HELM_SYNC_SERVER") is { Length: > 0 } custom && Uri.TryCreate(custom.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            ? uri
            : new Uri("https://sync.huyhung1404.com/");
}

/// <summary>
/// Every call of the Helm sync Worker (server/sync-worker, docs/sync-protocol.md). Server faults and throttling
/// surface as <see cref="HttpRequestException"/>, a rejected token as <see cref="SyncAuthException"/>, a full quota as
/// <see cref="SyncQuotaException"/> and other refusals as <see cref="SyncRequestException"/>.
/// </summary>
public sealed class SyncApiClient : IDisposable
{
    private readonly HttpClient _http;

    public SyncApiClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Helm-Sync/1");
    }

    public void Dispose() => _http.Dispose();

    public async Task<IReadOnlyList<PushOutcome>> PushAsync(SyncCredentials credentials, IReadOnlyList<PushItem> items, CancellationToken ct)
    {
        var body = new PushRequest(items.Select(i => new PushItemJson(i.Collection, i.Id, i.BaseVersion, i.Deleted, i.Payload)).ToList());
        return (await SendAsync<PushResponse>(credentials, HttpMethod.Post, "v1/sync/push", body, ct).ConfigureAwait(false)).Outcomes;
    }

    public Task<PullPage> PullAsync(SyncCredentials credentials, long sinceSeq, int limit, CancellationToken ct) =>
        SendAsync<PullPage>(credentials, HttpMethod.Get, $"v1/sync/pull?since={sinceSeq}&limit={limit}", null, ct);

    /// <summary>Which account a token opens, its name and scopes, and the account's storage use.</summary>
    public async Task<SyncAccountInfo> GetAccountAsync(SyncCredentials credentials, CancellationToken ct = default)
    {
        var me = await SendAsync<MeResponse>(credentials, HttpMethod.Get, "v1/me", null, ct).ConfigureAwait(false);
        return new SyncAccountInfo(me.Account.AccountId, me.Account.Name, me.Token.Id, me.Token.Name, me.Token.Scopes,
            FromMs(me.Token.ExpiresAt), me.Account.UsedBytes, me.Account.QuotaBytes);
    }

    /// <summary>Turns a one-time invite code into a new account and this device's first token.</summary>
    public async Task<SyncCredentials> RedeemInviteAsync(Uri server, string invite, string accountName, string deviceName, CancellationToken ct = default)
    {
        var result = await SendAsync<RedeemResponse>(new SyncCredentials(server, ""), HttpMethod.Post, "v1/redeem",
            new RedeemRequest(invite.Trim(), accountName.Trim(), deviceName.Trim()), ct, authenticate: false).ConfigureAwait(false);
        return new SyncCredentials(server, result.Token.Token);
    }

    public async Task<SyncKeyringDocument?> GetKeyringAsync(SyncCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<SyncKeyringDocument>(credentials, HttpMethod.Get, "v1/keyring", null, ct).ConfigureAwait(false);
        }
        catch (SyncRequestException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <returns>False when another device stored a keyring first (compare-and-set lost).</returns>
    public async Task<bool> PutKeyringAsync(SyncCredentials credentials, long baseVersion, string data, CancellationToken ct = default)
    {
        try
        {
            await SendAsync<KeyringPutResponse>(credentials, HttpMethod.Put, "v1/keyring", new KeyringPutRequest(baseVersion, data), ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (SyncRequestException e) when (e.Status == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<SyncDeviceToken>> ListTokensAsync(SyncCredentials credentials, CancellationToken ct = default) =>
        (await SendAsync<TokensResponse>(credentials, HttpMethod.Get, "v1/tokens", null, ct).ConfigureAwait(false)).Tokens
            .Select(ToDevice).ToList();

    /// <summary>A token for another device of the same account. The plaintext token is returned only here.</summary>
    public async Task<(SyncDeviceToken Device, string Token)> CreateTokenAsync(
        SyncCredentials credentials, string name, int? expiresInDays = null, IReadOnlyList<string>? scopes = null, CancellationToken ct = default)
    {
        var created = await SendAsync<CreatedTokenJson>(credentials, HttpMethod.Post, "v1/tokens",
            new CreateTokenRequest(name.Trim(), expiresInDays, scopes), ct).ConfigureAwait(false);
        return (ToDevice(new TokenJson(created.Id, created.Name, created.Scopes, created.CreatedAt, created.ExpiresAt, created.LastUsedAt, created.RevokedAt)),
            created.Token);
    }

    public Task RevokeTokenAsync(SyncCredentials credentials, string tokenId, CancellationToken ct = default) =>
        SendAsync<JsonElement>(credentials, HttpMethod.Delete, $"v1/tokens/{Uri.EscapeDataString(tokenId)}", null, ct);

    private async Task<T> SendAsync<T>(SyncCredentials credentials, HttpMethod method, string path, object? body, CancellationToken ct, bool authenticate = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(credentials.Server, path));
        if (authenticate) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        if (body is not null) request.Content = JsonContent.Create(body, options: SyncJson.Wire);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(SyncJson.Options, ct).ConfigureAwait(false)
                ?? throw new HttpRequestException("The sync server sent an empty response.");
        }

        var error = await ReadErrorAsync(response, ct).ConfigureAwait(false);
        var message = error.Message ?? error.Error;
        if (authenticate && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SyncAuthException(response.StatusCode, error.Error, message);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && error.Error == "quota_exceeded")
            throw new SyncQuotaException(message);
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"Sync server error {(int)response.StatusCode} ({error.Error}).", null, response.StatusCode);
        throw new SyncRequestException(response.StatusCode, error.Error, message);
    }

    private static async Task<ErrorResponse> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorResponse>(SyncJson.Options, ct).ConfigureAwait(false)
                ?? new ErrorResponse("unknown", null);
        }
        catch (JsonException)
        {
            return new ErrorResponse("unknown", null);
        }
    }

    private static DateTimeOffset? FromMs(long? ms) => ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;

    private static SyncDeviceToken ToDevice(TokenJson t) =>
        new(t.Id, t.Name, t.Scopes, DateTimeOffset.FromUnixTimeMilliseconds(t.CreatedAt), FromMs(t.ExpiresAt), FromMs(t.LastUsedAt), FromMs(t.RevokedAt));

    private sealed record PushItemJson(string Collection, string Id, long BaseVersion, bool Deleted, byte[] Payload);
    private sealed record PushRequest(IReadOnlyList<PushItemJson> Items);
    private sealed record PushResponse(IReadOnlyList<PushOutcome> Outcomes);
    private sealed record MeAccount(string AccountId, string Name, long UsedBytes, long QuotaBytes);
    private sealed record TokenJson(string Id, string Name, IReadOnlyList<string> Scopes, long CreatedAt, long? ExpiresAt, long? LastUsedAt, long? RevokedAt);
    private sealed record MeResponse(MeAccount Account, TokenJson Token);
    private sealed record RedeemRequest(string Invite, string AccountName, string DeviceName);
    private sealed record CreatedTokenJson(string Token, string Id, string Name, IReadOnlyList<string> Scopes, long CreatedAt, long? ExpiresAt, long? LastUsedAt, long? RevokedAt);
    private sealed record RedeemResponse(MeAccount Account, CreatedTokenJson Token);
    private sealed record KeyringPutRequest(long BaseVersion, string Data);
    private sealed record KeyringPutResponse(bool Accepted, long Version);
    private sealed record TokensResponse(IReadOnlyList<TokenJson> Tokens, string CurrentTokenId);
    private sealed record CreateTokenRequest(string Name, int? ExpiresInDays, IReadOnlyList<string>? Scopes);
    private sealed record ErrorResponse(string Error, string? Message);
}

/// <summary><see cref="ISyncTransport"/> for the engine: the current device credentials plus <see cref="SyncApiClient"/>.</summary>
public sealed class HttpSyncTransport(ISyncCredentialStore credentials, SyncApiClient api) : ISyncTransport
{
    public bool IsConfigured => credentials.Load() is not null;

    public Task<IReadOnlyList<PushOutcome>> PushAsync(IReadOnlyList<PushItem> items, CancellationToken ct) => api.PushAsync(Current(), items, ct);

    public Task<PullPage> PullAsync(long sinceSeq, int limit, CancellationToken ct) => api.PullAsync(Current(), sinceSeq, limit, ct);

    private SyncCredentials Current() =>
        credentials.Load() ?? throw new InvalidOperationException("Sync is not configured on this device.");
}
