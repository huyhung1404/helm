using System.Net;
using System.Text;
using Helm.Core.Sync;

namespace Helm.Tests;

public sealed class SyncTransportTests
{
    private const string ValidToken = "helm_pat_0123456789abcdef_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0MfsOR";

    [Fact]
    public void Checksum_matches_the_vector_shared_with_the_worker()
    {
        // server/sync-worker/test/token.test.ts asserts the same literal.
        Assert.Equal("0MfsOR", SyncToken.Checksum("helm_pat_0123456789abcdef_" + new string('A', 40)));
        Assert.Equal(0xCBF43926u, SyncToken.Crc32("123456789"u8));
    }

    [Fact]
    public void Tokens_parse_and_typos_are_caught_offline()
    {
        Assert.True(SyncToken.TryParse(ValidToken, out var accountId));
        Assert.Equal("0123456789abcdef", accountId);

        Assert.False(SyncToken.TryParse(ValidToken.Replace("AAAA0MfsOR", "AAAB0MfsOR"), out _));
        Assert.False(SyncToken.TryParse(ValidToken[..^1], out _));
        Assert.False(SyncToken.TryParse("ghp_" + ValidToken[9..], out _));
        Assert.False(SyncToken.TryParse(null, out _));
        Assert.Equal("helm_pat_01…sOR", SyncToken.Redact(ValidToken));
    }

    [Fact]
    public void Invite_codes_are_recognised_offline()
    {
        const string body = "helm_inv_" + "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var code = body + SyncToken.Checksum(body);
        Assert.True(SyncToken.IsInviteCode(code));
        Assert.False(SyncToken.IsInviteCode(code[..^1] + (code[^1] == 'a' ? 'b' : 'a')));
        Assert.False(SyncToken.IsInviteCode(ValidToken));
    }

    [Fact]
    public async Task Optional_request_fields_are_omitted_not_sent_as_null()
    {
        string? body = null;
        var api = new SyncApiClient(new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.Created,
                """{"token":"t","id":"tok_x","name":"Laptop","scopes":["sync:read"],"createdAt":1,"expiresAt":null,"lastUsedAt":null,"revokedAt":null}""");
        }));

        await api.CreateTokenAsync(new SyncCredentials(new Uri("https://sync.example/"), ValidToken), "Laptop");

        Assert.Equal("""{"name":"Laptop"}""", body);
    }

    [Fact]
    public async Task Pull_decodes_base64_payloads()
    {
        var transport = Transport(_ => Json(HttpStatusCode.OK,
            """{"records":[{"collection":"notes","id":"n1","version":2,"seq":7,"deleted":false,"payload":"AQID"}],"nextSeq":7,"hasMore":false}"""));

        var page = await transport.PullAsync(0, 500, SyncPullFilter.None, CancellationToken.None);

        var record = Assert.Single(page.Records);
        Assert.Equal(new byte[] { 1, 2, 3 }, record.Payload);
        Assert.Equal((2L, 7L, 7L), (record.Version, record.Seq, page.NextSeq));
    }

    [Fact]
    public async Task Push_sends_bearer_token_and_base64_payload()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var transport = Transport(request =>
        {
            seen = request;
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, """{"outcomes":[{"collection":"notes","id":"n1","accepted":true,"version":1,"seq":1,"current":null}]}""");
        });

        var outcomes = await transport.PushAsync([new PushItem("notes", "n1", 0, false, [1, 2, 3])], CancellationToken.None);

        Assert.True(Assert.Single(outcomes).Accepted);
        Assert.Equal("Bearer " + ValidToken, seen!.Headers.Authorization!.ToString());
        Assert.Equal("https://sync.example/v1/sync/push", seen.RequestUri!.ToString());
        Assert.Contains("\"payload\":\"AQID\"", body);
        Assert.Contains("\"baseVersion\":0", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_token", SyncRunOutcome.Unauthorized, SyncState.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "insufficient_scope", SyncRunOutcome.Unauthorized, SyncState.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "internal", SyncRunOutcome.Offline, SyncState.Offline)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", SyncRunOutcome.Offline, SyncState.Offline)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_item", SyncRunOutcome.Failed, SyncState.Error)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "quota_exceeded", SyncRunOutcome.QuotaExceeded, SyncState.QuotaExceeded)]
    public async Task Server_errors_map_to_engine_states(HttpStatusCode status, string code, SyncRunOutcome outcome, SyncState state)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new SyncDatabase(Path.Combine(dir, "a.db"), TestKeys.Local);
            var transport = Transport(_ => Json(status, $$"""{"error":"{{code}}"}"""));
            using var engine = new SyncEngine(db, transport, new InMemoryMasterKeyStore(SyncKeyring.CreateMasterKey()), [],
                debounce: TimeSpan.FromHours(1));
            using var notes = new SyncedCollection<Note>(engine, new() { Name = "notes" });
            notes.Add(new Note("t"));

            var result = await engine.SyncNowAsync();

            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(state, engine.Status.State);
            Assert.Single(notes.All()); // local data is never touched by a failed sync
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Not_configured_until_credentials_exist()
    {
        var store = new InMemorySyncCredentialStore();
        var transport = new HttpSyncTransport(store, new SyncApiClient());
        Assert.False(transport.IsConfigured);

        store.Save(new SyncCredentials(new Uri("https://sync.example/"), ValidToken));
        Assert.True(transport.IsConfigured);
    }

    [Fact]
    public void Dpapi_credentials_round_trip_and_refuse_malformed_tokens()
    {
        var path = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"), "credentials.bin");
        try
        {
            var store = new ProtectedSyncCredentialStore(path, new DpapiSecretProtector());
            var credentials = new SyncCredentials(new Uri("https://sync.example/"), ValidToken);
            store.Save(credentials);

            Assert.Equal(credentials, new ProtectedSyncCredentialStore(path, new DpapiSecretProtector()).Load());
            Assert.DoesNotContain(ValidToken, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.Throws<ArgumentException>(() => store.Save(credentials with { Token = "helm_pat_nope" }));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private const string Blob = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public async Task Presigned_uploads_go_straight_to_storage_without_the_token_and_are_recorded_after()
    {
        var log = new List<string>();
        var stored = new Dictionary<string, byte[]>();
        var transport = Transport(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "r2.example")
            {
                Assert.Null(request.Headers.Authorization);
                var bytes = request.Content!.ReadAsByteArrayAsync().Result;
                Assert.Equal(bytes.Length, request.Content.Headers.ContentLength);
                stored[path] = bytes;
                log.Add("r2 " + path);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            log.Add(request.Method + " " + path + " " + request.Content?.ReadAsStringAsync().Result);
            return path.EndsWith("/upload-urls")
                ? Json(HttpStatusCode.OK, """{"urls":[{"index":0,"url":"https://r2.example/c0?sig=1"},{"index":2,"url":"https://r2.example/c2?sig=1"}],"expiresAt":1}""")
                : Json(HttpStatusCode.OK, """{"chunks":[]}""");
        });

        await transport.PutChunksAsync(Blob, [(0, 3), (2, 2)], (i, _) => Task.FromResult(i == 0 ? new byte[] { 1, 2, 3 } : [9, 9]), CancellationToken.None);

        Assert.Equal([
            $$"""POST /v1/blobs/{{Blob}}/upload-urls {"chunks":[{"index":0,"length":3},{"index":2,"length":2}]}""",
            "r2 /c0", "r2 /c2",
            $$"""POST /v1/blobs/{{Blob}}/uploaded {"indexes":[0,2]}""",
        ], log);
        Assert.Equal([1, 2, 3], stored["/c0"]);
    }

    [Fact]
    public async Task Without_presigning_on_the_server_chunks_take_the_worker_route_and_it_is_not_asked_again()
    {
        var log = new List<string>();
        var transport = Transport(request =>
        {
            log.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/upload-urls")
                ? Json(HttpStatusCode.ServiceUnavailable, """{"error":"presign_disabled"}""")
                : Json(HttpStatusCode.OK, """{"id":"x","index":0,"size":1}""");
        });

        await transport.PutChunksAsync(Blob, [(0, 1)], (_, _) => Task.FromResult(new byte[] { 7 }), CancellationToken.None);
        await transport.PutChunksAsync(Blob, [(1, 1)], (_, _) => Task.FromResult(new byte[] { 8 }), CancellationToken.None);

        Assert.Equal([$"POST /v1/blobs/{Blob}/upload-urls", $"PUT /v1/blobs/{Blob}/chunks/0", $"PUT /v1/blobs/{Blob}/chunks/1"], log);
        Assert.False(transport.PresignEnabled);
    }

    [Fact]
    public async Task A_presigned_url_storage_refuses_falls_back_to_the_worker_for_that_chunk()
    {
        var log = new List<string>();
        var transport = Transport(request =>
        {
            log.Add(request.Method + " " + request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            if (request.RequestUri.Host == "r2.example") return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return request.RequestUri.AbsolutePath.EndsWith("/upload-urls")
                ? Json(HttpStatusCode.OK, """{"urls":[{"index":0,"url":"https://r2.example/c0"}],"expiresAt":1}""")
                : Json(HttpStatusCode.OK, """{"id":"x","index":0,"size":1}""");
        });

        await transport.PutChunksAsync(Blob, [(0, 1)], (_, _) => Task.FromResult(new byte[] { 7 }), CancellationToken.None);

        Assert.Equal([$"POST sync.example/v1/blobs/{Blob}/upload-urls", "PUT r2.example/c0", $"PUT sync.example/v1/blobs/{Blob}/chunks/0"], log);
        Assert.False(transport.PresignEnabled);
    }

    [Fact]
    public async Task Downloads_ask_for_the_urls_once_per_blob_and_read_from_storage()
    {
        var log = new List<string>();
        var transport = Transport(request =>
        {
            log.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            if (request.RequestUri.Host == "r2.example")
            {
                Assert.Null(request.Headers.Authorization);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([(byte)request.RequestUri.AbsolutePath[^1]]) };
            }
            var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
            return Json(HttpStatusCode.OK, $$"""{"chunkCount":2,"urls":[{"index":0,"size":1,"url":"https://r2.example/a"},{"index":1,"size":1,"url":"https://r2.example/b"}],"expiresAt":{{expires}}}""");
        });

        var first = await transport.GetChunkAsync(Blob, 0, CancellationToken.None);
        var second = await transport.GetChunkAsync(Blob, 1, CancellationToken.None);
        Assert.Equal(new byte[] { (byte)'a' }, first);
        Assert.Equal(new byte[] { (byte)'b' }, second);
        Assert.Equal([$"sync.example/v1/blobs/{Blob}/download-urls", "r2.example/a", "r2.example/b"], log);
    }

    [Fact]
    public async Task A_cleaned_up_cursor_surfaces_as_resync_required_and_filters_are_sent()
    {
        string? query = null;
        var transport = Transport(request =>
        {
            query = request.RequestUri!.Query;
            return Json(HttpStatusCode.Gone, """{"error":"resync_required","message":"Pull again from 0.","purgedSeq":9}""");
        });

        await Assert.ThrowsAsync<SyncResyncRequiredException>(() =>
            transport.PullAsync(3, 500, new SyncPullFilter(Exclude: ["vault.", "notes.items"]), CancellationToken.None));
        Assert.Equal("?since=3&limit=500&exclude=vault.%2Cnotes.items", query);
    }

    private sealed record Note(string Title);

    private static HttpSyncTransport Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new InMemorySyncCredentialStore(new SyncCredentials(new Uri("https://sync.example/"), ValidToken)),
            new SyncApiClient(new StubHandler(respond)));

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
