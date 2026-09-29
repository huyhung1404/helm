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

    public async Task<PullPage> PullAsync(SyncCredentials credentials, long sinceSeq, int limit, SyncPullFilter filter, CancellationToken ct)
    {
        var path = $"v1/sync/pull?since={sinceSeq}&limit={limit}";
        if (filter.Only is { Count: > 0 } only) path += "&only=" + Uri.EscapeDataString(string.Join(',', only));
        else if (filter.Exclude is { Count: > 0 } exclude) path += "&exclude=" + Uri.EscapeDataString(string.Join(',', exclude));
        try
        {
            return await SendAsync<PullPage>(credentials, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        }
        catch (SyncRequestException e) when (e.Code == "resync_required")
        {
            throw new SyncResyncRequiredException(e.Message);
        }
    }

    /// <summary>Opens <c>GET /v1/sync/live</c>, the WebSocket that announces new seqs.</summary>
    public async Task<System.Net.WebSockets.WebSocket> ConnectLiveAsync(SyncCredentials credentials, CancellationToken ct)
    {
        var http = new Uri(credentials.Server, "v1/sync/live");
        var uri = new UriBuilder(http) { Scheme = http.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Port = http.IsDefaultPort ? -1 : http.Port }.Uri;
        var socket = new System.Net.WebSockets.ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Token);
        socket.Options.SetRequestHeader("User-Agent", "Helm-Sync/1");
        socket.Options.CollectHttpResponseDetails = true;
        // Keep-alive is the channel's own "ping" text, which the server answers without waking the account's object.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            return socket;
        }
        catch (System.Net.WebSockets.WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            socket.Dispose();
            throw new SyncAuthException(socket.HttpStatusCode, "invalid_token", "The server did not accept this token.");
        }
        catch (System.Net.WebSockets.WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            socket.Dispose();
            throw new SyncLiveUnavailableException("This sync server has no live channel.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

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

    /// <summary>The server's effective limits; a server from before blobs reports none, and the defaults apply.</summary>
    public async Task<SyncLimits> GetLimitsAsync(SyncCredentials credentials, CancellationToken ct = default)
    {
        var me = await SendAsync<MeResponse>(credentials, HttpMethod.Get, "v1/me", null, ct).ConfigureAwait(false);
        const long chunk = 4 * 1024 * 1024 + 4096;
        return me.Limits is { } l
            ? new SyncLimits(l.MaxChunkBytes, l.MaxChunksPerBlob, l.MaxBlobBytes, l.MaxPayloadBytes)
            : new SyncLimits(chunk, 65, 65 * chunk, 1024 * 1024);
    }

    public async Task<RemoteBlob> ReserveBlobAsync(SyncCredentials credentials, string id, long size, int chunkCount, CancellationToken ct) =>
        ToBlob(await SendAsync<BlobJson>(credentials, HttpMethod.Post, "v1/blobs", new ReserveBlobRequest(id, size, chunkCount), ct).ConfigureAwait(false));

    public async Task PutBlobChunkAsync(SyncCredentials credentials, string id, int index, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var content = new ReadOnlyMemoryContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = data.Length;
        await SendAsync<JsonElement>(credentials, HttpMethod.Put, BlobPath(id) + $"/chunks/{index}", null, ct, content: content).ConfigureAwait(false);
    }

    public async Task<RemoteBlob> CommitBlobAsync(SyncCredentials credentials, string id, CancellationToken ct) =>
        ToBlob(await SendAsync<BlobJson>(credentials, HttpMethod.Post, BlobPath(id) + "/commit", null, ct).ConfigureAwait(false));

    public async Task<RemoteBlob?> GetBlobAsync(SyncCredentials credentials, string id, CancellationToken ct)
    {
        try
        {
            return ToBlob(await SendAsync<BlobJson>(credentials, HttpMethod.Get, BlobPath(id), null, ct).ConfigureAwait(false));
        }
        catch (SyncRequestException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<RemoteBlobPage> ListBlobsAsync(SyncCredentials credentials, string? after, int limit, CancellationToken ct)
    {
        var query = $"v1/blobs?limit={limit}" + (after is null ? "" : "&after=" + Uri.EscapeDataString(after));
        var page = await SendAsync<BlobPageJson>(credentials, HttpMethod.Get, query, null, ct).ConfigureAwait(false);
        return new RemoteBlobPage(page.Blobs.Select(ToBlob).ToList(), page.HasMore, page.Next);
    }

    /// <returns>Null when the server has no such chunk.</returns>
    public async Task<byte[]?> GetBlobChunkAsync(SyncCredentials credentials, string id, int index, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(credentials.Server, BlobPath(id) + $"/chunks/{index}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, authenticate: true, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public Task DeleteBlobAsync(SyncCredentials credentials, string id, CancellationToken ct) =>
        SendAsync<JsonElement>(credentials, HttpMethod.Delete, BlobPath(id), null, ct);

    public Task RestoreBlobAsync(SyncCredentials credentials, string id, CancellationToken ct) =>
        SendAsync<JsonElement>(credentials, HttpMethod.Post, BlobPath(id) + "/restore", null, ct);

    /// <summary>Presigned R2 URLs to PUT these chunks to (503 presign_disabled when the server has no R2 keys).</summary>
    public async Task<IReadOnlyDictionary<int, Uri>> GetUploadUrlsAsync(SyncCredentials credentials, string id,
        IReadOnlyList<(int Index, long Length)> chunks, CancellationToken ct)
    {
        var body = new UploadUrlsRequest(chunks.Select(c => new ChunkLengthJson(c.Index, c.Length)).ToList());
        var reply = await SendAsync<UrlsResponse>(credentials, HttpMethod.Post, BlobPath(id) + "/upload-urls", body, ct).ConfigureAwait(false);
        return reply.Urls.ToDictionary(u => u.Index, u => new Uri(u.Url));
    }

    /// <summary>After presigned PUTs: the server checks R2 and records the chunks (409 chunk_missing names the ones to redo).</summary>
    public Task RecordUploadedAsync(SyncCredentials credentials, string id, IReadOnlyList<int> indexes, CancellationToken ct) =>
        SendAsync<JsonElement>(credentials, HttpMethod.Post, BlobPath(id) + "/uploaded", new UploadedRequest(indexes), ct);

    /// <summary>Presigned R2 URLs of every chunk of a committed blob, valid for a few minutes.</summary>
    public async Task<(IReadOnlyDictionary<int, Uri> Urls, DateTimeOffset ExpiresAt)> GetDownloadUrlsAsync(SyncCredentials credentials, string id,
        CancellationToken ct)
    {
        var reply = await SendAsync<UrlsResponse>(credentials, HttpMethod.Get, BlobPath(id) + "/download-urls", null, ct).ConfigureAwait(false);
        return (reply.Urls.ToDictionary(u => u.Index, u => new Uri(u.Url)), DateTimeOffset.FromUnixTimeMilliseconds(reply.ExpiresAt));
    }

    /// <summary>PUT to a presigned URL: no Helm token, exactly the signed Content-Length.</summary>
    /// <exception cref="HttpRequestException">R2 refused the upload (expired or wrong signature, network).</exception>
    public async Task PutToUrlAsync(Uri url, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ReadOnlyMemoryContent(data) };
        request.Content.Headers.ContentLength = data.Length;
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Storage refused the upload ({(int)response.StatusCode}).", null, response.StatusCode);
    }

    /// <returns>Null when storage has no such object.</returns>
    public async Task<byte[]?> GetFromUrlAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Storage refused the download ({(int)response.StatusCode}).", null, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static string BlobPath(string id) => "v1/blobs/" + Uri.EscapeDataString(id);

    private static RemoteBlob ToBlob(BlobJson b) => new(b.Id, b.Size, b.ChunkCount,
        b.State switch { "committed" => RemoteBlobState.Committed, "purging" => RemoteBlobState.Purging, _ => RemoteBlobState.Pending },
        DateTimeOffset.FromUnixTimeMilliseconds(b.CreatedAt), FromMs(b.DeletedAt), b.Chunks ?? []);

    private async Task<T> SendAsync<T>(SyncCredentials credentials, HttpMethod method, string path, object? body, CancellationToken ct,
        bool authenticate = true, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(credentials.Server, path));
        if (authenticate) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        if (body is not null) request.Content = JsonContent.Create(body, options: SyncJson.Wire);
        else if (content is not null) request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(SyncJson.Options, ct).ConfigureAwait(false)
                ?? throw new HttpRequestException("The sync server sent an empty response.");
        }
        await ThrowAsync(response, authenticate, ct).ConfigureAwait(false);
        throw new HttpRequestException("Unreachable.");
    }

    private static async Task ThrowAsync(HttpResponseMessage response, bool authenticate, CancellationToken ct)
    {
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
    private sealed record LimitsJson(long MaxChunkBytes, int MaxChunksPerBlob, long MaxBlobBytes, long MaxPayloadBytes);
    private sealed record MeResponse(MeAccount Account, TokenJson Token, LimitsJson? Limits = null);
    private sealed record ReserveBlobRequest(string Id, long Size, int ChunkCount);
    private sealed record BlobJson(string Id, long Size, int ChunkCount, string State, long CreatedAt, long? CommittedAt, long? DeletedAt, IReadOnlyList<int>? Chunks);
    private sealed record BlobPageJson(IReadOnlyList<BlobJson> Blobs, bool HasMore, string? Next);
    private sealed record RedeemRequest(string Invite, string AccountName, string DeviceName);
    private sealed record CreatedTokenJson(string Token, string Id, string Name, IReadOnlyList<string> Scopes, long CreatedAt, long? ExpiresAt, long? LastUsedAt, long? RevokedAt);
    private sealed record RedeemResponse(MeAccount Account, CreatedTokenJson Token);
    private sealed record KeyringPutRequest(long BaseVersion, string Data);
    private sealed record KeyringPutResponse(bool Accepted, long Version);
    private sealed record TokensResponse(IReadOnlyList<TokenJson> Tokens, string CurrentTokenId);
    private sealed record CreateTokenRequest(string Name, int? ExpiresInDays, IReadOnlyList<string>? Scopes);
    private sealed record ErrorResponse(string Error, string? Message);
    private sealed record ChunkLengthJson(int Index, long Length);
    private sealed record UploadUrlsRequest(IReadOnlyList<ChunkLengthJson> Chunks);
    private sealed record UploadedRequest(IReadOnlyList<int> Indexes);
    private sealed record UrlJson(int Index, string Url);
    private sealed record UrlsResponse(IReadOnlyList<UrlJson> Urls, long ExpiresAt);
}

/// <summary>
/// <see cref="ISyncTransport"/>, <see cref="IBlobTransport"/> and <see cref="ISyncLiveTransport"/> for the engine: the
/// current device credentials plus <see cref="SyncApiClient"/>.
/// </summary>
/// <remarks>
/// Blob chunks go straight to and from R2 through presigned URLs when the server offers them, so large files never
/// pass through the Worker. A server without R2 keys (or older) answers those routes with an error; the chunks then
/// take the Worker routes as before, and presigned URLs are not tried again for <see cref="PresignRetryAfter"/>.
/// </remarks>
public sealed class HttpSyncTransport(ISyncCredentialStore credentials, SyncApiClient api, TimeProvider? time = null)
    : ISyncTransport, IBlobTransport, ISyncLiveTransport
{
    /// <summary>Chunks per request for presigned URLs (each chunk is up to 4 MiB).</summary>
    private const int PresignBatch = 16;
    internal static readonly TimeSpan PresignRetryAfter = TimeSpan.FromHours(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _presignOffUntil = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (IReadOnlyDictionary<int, Uri> Urls, DateTimeOffset ExpiresAt)> _downloads = new(StringComparer.Ordinal);

    public bool IsConfigured => credentials.Load() is not null;

    public Task<IReadOnlyList<PushOutcome>> PushAsync(IReadOnlyList<PushItem> items, CancellationToken ct) => api.PushAsync(Current(), items, ct);

    public Task<PullPage> PullAsync(long sinceSeq, int limit, SyncPullFilter filter, CancellationToken ct) =>
        api.PullAsync(Current(), sinceSeq, limit, filter, ct);

    public Task<System.Net.WebSockets.WebSocket> ConnectLiveAsync(CancellationToken ct) => api.ConnectLiveAsync(Current(), ct);

    /// <summary>True while presigned URLs are worth trying (not refused by this server recently).</summary>
    internal bool PresignEnabled
    {
        get { lock (_gate) return _time.GetUtcNow() >= _presignOffUntil; }
    }

    public async Task PutChunksAsync(string id, IReadOnlyList<(int Index, long Length)> chunks, Func<int, CancellationToken, Task<byte[]>> read,
        CancellationToken ct)
    {
        var credentials = Current();
        foreach (var batch in chunks.Chunk(PresignBatch))
        {
            IReadOnlyDictionary<int, Uri>? urls = null;
            if (PresignEnabled)
            {
                try
                {
                    urls = await api.GetUploadUrlsAsync(credentials, id, batch, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (IsPresignUnavailable(e))
                {
                    TurnPresignOff();
                }
            }
            var direct = new List<int>();
            foreach (var (index, _) in batch)
            {
                var data = await read(index, ct).ConfigureAwait(false);
                if (urls is not null && urls.TryGetValue(index, out var url))
                {
                    try
                    {
                        await api.PutToUrlAsync(url, data, ct).ConfigureAwait(false);
                        direct.Add(index);
                        continue;
                    }
                    catch (HttpRequestException e) when (e.StatusCode is { } status && (int)status is >= 400 and < 500)
                    {
                        // A signature R2 does not accept: this server's presigning is broken; use the Worker.
                        TurnPresignOff();
                        urls = null;
                    }
                }
                await api.PutBlobChunkAsync(credentials, id, index, data, ct).ConfigureAwait(false);
            }
            if (direct.Count > 0) await api.RecordUploadedAsync(credentials, id, direct, ct).ConfigureAwait(false);
        }
    }

    public Task<SyncLimits> GetLimitsAsync(CancellationToken ct) => api.GetLimitsAsync(Current(), ct);

    public Task<RemoteBlob> ReserveAsync(string id, long size, int chunkCount, CancellationToken ct) => api.ReserveBlobAsync(Current(), id, size, chunkCount, ct);

    public Task PutChunkAsync(string id, int index, ReadOnlyMemory<byte> data, CancellationToken ct) => api.PutBlobChunkAsync(Current(), id, index, data, ct);

    public Task<RemoteBlob> CommitAsync(string id, CancellationToken ct) => api.CommitBlobAsync(Current(), id, ct);

    public Task<RemoteBlob?> GetAsync(string id, CancellationToken ct) => api.GetBlobAsync(Current(), id, ct);

    public Task<RemoteBlobPage> ListAsync(string? after, int limit, CancellationToken ct) => api.ListBlobsAsync(Current(), after, limit, ct);

    public async Task<byte[]?> GetChunkAsync(string id, int index, CancellationToken ct)
    {
        var credentials = Current();
        if (PresignEnabled)
        {
            try
            {
                var urls = await DownloadUrlsAsync(credentials, id, ct).ConfigureAwait(false);
                if (urls is not null && urls.TryGetValue(index, out var url)) return await api.GetFromUrlAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsPresignUnavailable(e) || e is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500)
            {
                // No presigning here (older server, no R2 keys), or storage refused the signed URL: use the Worker.
                TurnPresignOff();
            }
        }
        return await api.GetBlobChunkAsync(credentials, id, index, ct).ConfigureAwait(false);
    }

    /// <summary>The blob's download URLs, cached until shortly before they expire. Null when the blob is not committed.</summary>
    private async Task<IReadOnlyDictionary<int, Uri>?> DownloadUrlsAsync(SyncCredentials credentials, string id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_downloads.TryGetValue(id, out var cached) && cached.ExpiresAt - _time.GetUtcNow() > TimeSpan.FromMinutes(1)) return cached.Urls;
        }
        try
        {
            var fresh = await api.GetDownloadUrlsAsync(credentials, id, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_downloads.Count > 64) _downloads.Clear();
                _downloads[id] = fresh;
            }
            return fresh.Urls;
        }
        catch (SyncRequestException e) when (e.Code == "blob_not_found")
        {
            return null;
        }
    }

    /// <summary>
    /// The server cannot presign: 503 presign_disabled (no R2 keys; 5xx surface as HttpRequestException), or an older
    /// server without the routes (404, 405).
    /// </summary>
    private static bool IsPresignUnavailable(Exception e) => e switch
    {
        SyncRequestException r => r.Code is "presign_disabled" or "not_found" or "method_not_allowed",
        HttpRequestException h => h.StatusCode == HttpStatusCode.ServiceUnavailable,
        _ => false,
    };

    private void TurnPresignOff()
    {
        lock (_gate)
        {
            _presignOffUntil = _time.GetUtcNow() + PresignRetryAfter;
            _downloads.Clear();
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct) => api.DeleteBlobAsync(Current(), id, ct);

    public Task RestoreAsync(string id, CancellationToken ct) => api.RestoreBlobAsync(Current(), id, ct);

    private SyncCredentials Current() =>
        credentials.Load() ?? throw new InvalidOperationException("Sync is not configured on this device.");
}
