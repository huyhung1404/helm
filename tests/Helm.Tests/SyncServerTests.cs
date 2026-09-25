using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>Runs only when HELM_SYNC_TEST_URL and HELM_SYNC_TEST_ADMIN point at a sync Worker (e.g. wrangler dev).</summary>
public sealed class ServerFactAttribute : FactAttribute
{
    public ServerFactAttribute()
    {
        if (SyncServerTests.ServerUrl is null || SyncServerTests.AdminToken is null)
            Skip = "Set HELM_SYNC_TEST_URL and HELM_SYNC_TEST_ADMIN to run against a sync Worker (see server/sync-worker/README.md).";
    }
}

/// <summary>
/// End-to-end: the real engine, crypto and HTTP transport against the real Worker and Durable Object. Each test
/// creates its own account through the admin API, so runs never interfere.
/// </summary>
public sealed class SyncServerTests : IDisposable
{
    internal static readonly Uri? ServerUrl =
        Environment.GetEnvironmentVariable("HELM_SYNC_TEST_URL") is { Length: > 0 } url ? new Uri(url.TrimEnd('/') + "/") : null;
    internal static readonly string? AdminToken = Environment.GetEnvironmentVariable("HELM_SYNC_TEST_ADMIN") is { Length: > 0 } t ? t : null;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = SyncKeyring.CreateMasterKey();
    private readonly List<IDisposable> _owned = [];
    private readonly HttpClient _admin = new();

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _admin.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [ServerFact]
    public async Task Two_devices_converge_through_the_worker_and_conflicts_keep_both()
    {
        var accountId = await CreateAccountAsync();
        var a = NewDevice("a", await CreateTokenAsync(accountId, "PC"));
        var b = NewDevice("b", await CreateTokenAsync(accountId, "Laptop"));

        var id = a.Notes.Add(new Note("Plan", "v1"));
        await SyncAll(a, b);
        Assert.Equal(new Note("Plan", "v1"), b.Notes.Get(id));

        a.Notes.Upsert(id, new Note("Plan", "edited on A"));
        b.Notes.Upsert(id, new Note("Plan", "edited on B"));
        await SyncAll(a, b, a);
        foreach (var device in new[] { a, b })
        {
            Assert.Equal(new Note("Plan", "edited on A"), device.Notes.Get(id));
            Assert.Contains(device.Notes.All(), n => n.Value == new Note("Plan (conflict)", "edited on B"));
        }

        b.Notes.Delete(id);
        await SyncAll(b, a);
        Assert.Null(a.Notes.Get(id));
    }

    [ServerFact]
    public async Task Large_uploads_page_through_the_worker()
    {
        var accountId = await CreateAccountAsync();
        var a = NewDevice("a", await CreateTokenAsync(accountId, "a"));
        var b = NewDevice("b", await CreateTokenAsync(accountId, "b"));
        for (var i = 0; i < 1200; i++) a.Notes.Upsert($"n{i:0000}", new Note($"#{i}", new string('x', 200)));

        Assert.Equal(1200, (await a.Engine.SyncNowAsync()).Pushed);
        Assert.Equal(1200, (await b.Engine.SyncNowAsync()).Pulled);
        Assert.Equal(1200, b.Notes.All().Count);
    }

    [ServerFact]
    public async Task Revoked_and_read_only_tokens_are_refused_and_local_data_is_kept()
    {
        var accountId = await CreateAccountAsync();
        var (token, tokenId) = await CreateTokenWithIdAsync(accountId, "lost laptop");
        var device = NewDevice("lost", token);
        device.Notes.Add(new Note("t", "b"));
        Assert.Equal(SyncRunOutcome.Completed, (await device.Engine.SyncNowAsync()).Outcome);

        using (var revoke = new HttpRequestMessage(HttpMethod.Delete, new Uri(ServerUrl!, $"admin/accounts/{accountId}/tokens/{tokenId}")))
        {
            revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            (await _admin.SendAsync(revoke)).EnsureSuccessStatusCode();
        }
        device.Notes.Add(new Note("after revoke", "b"));
        Assert.Equal(SyncRunOutcome.Unauthorized, (await device.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(2, device.Notes.All().Count);

        var viewer = NewDevice("viewer", await CreateTokenAsync(accountId, "viewer", scopes: ["sync:read"]));
        viewer.Notes.Add(new Note("cannot push", "b"));
        Assert.Equal(SyncRunOutcome.Unauthorized, (await viewer.Engine.SyncNowAsync()).Outcome);
    }

    [ServerFact]
    public async Task Account_info_names_the_token_and_bad_tokens_are_rejected()
    {
        var accountId = await CreateAccountAsync("Anh");
        var token = await CreateTokenAsync(accountId, "PC nhà");
        using var api = new SyncApiClient();

        var info = await api.GetAccountAsync(new SyncCredentials(ServerUrl!, token));
        Assert.Equal((accountId, "Anh", "PC nhà"), (info.AccountId, info.AccountName, info.TokenName));
        Assert.Equal(["sync:read", "sync:write", "tokens:manage"], info.Scopes);

        // Well-formed (valid checksum) but never issued: the server must refuse it.
        var forged = ForgeToken(accountId);
        var error = await Assert.ThrowsAsync<SyncAuthException>(() => api.GetAccountAsync(new SyncCredentials(ServerUrl!, forged)));
        Assert.Equal("invalid_token", error.Code);
    }

    [ServerFact]
    public async Task Admin_api_refuses_a_wrong_admin_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ServerUrl!, "admin/accounts"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken + "x");
        var response = await _admin.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [ServerFact]
    public async Task Invite_passphrase_second_device_recovery_and_revocation_end_to_end()
    {
        var invite = await CreateInviteAsync(quotaMb: 50);

        // Device A redeems the invite, creates the account passphrase and gets the recovery key.
        var a = NewSetupDevice("a");
        var account = await a.Setup.RedeemInviteAsync(invite, "Minh", "PC nhà", ServerUrl);
        Assert.Equal(("Minh", "PC nhà", 50L * 1024 * 1024), (account.AccountName, account.TokenName, account.QuotaBytes));
        Assert.True(account.CanManageTokens);
        Assert.Equal(SyncSetupStage.NeedsKey, a.Setup.Stage);
        Assert.False(await a.Setup.HasPassphraseAsync());
        var recovery = await a.Setup.CreatePassphraseAsync("minh's long passphrase");
        Assert.Equal(SyncSetupStage.Ready, a.Setup.Stage);
        var id = a.Notes.Add(new Note("Plan", "from A"));
        Assert.Equal(SyncRunOutcome.Completed, (await a.Engine.SyncNowAsync()).Outcome);

        // An invite works once.
        await Assert.ThrowsAsync<SyncRequestException>(() => NewSetupDevice("x").Setup.RedeemInviteAsync(invite, "Again", "x", ServerUrl));

        // A adds device B; B unlocks with the passphrase and sees A's data.
        var (deviceB, tokenB) = await a.Setup.AddDeviceAsync("Laptop");
        var b = NewSetupDevice("b");
        await b.Setup.ConnectWithTokenAsync(tokenB, ServerUrl);
        Assert.True(await b.Setup.HasPassphraseAsync());
        await Assert.ThrowsAsync<SyncKeyException>(() => b.Setup.UnlockWithPassphraseAsync("wrong passphrase!!"));
        await b.Setup.UnlockWithPassphraseAsync("minh's long passphrase");
        Assert.Equal(new Note("Plan", "from A"), b.Notes.Get(id));

        // Device C recovers with the recovery key alone.
        var (_, tokenC) = await a.Setup.AddDeviceAsync("Phone");
        var c = NewSetupDevice("c");
        await c.Setup.ConnectWithTokenAsync(tokenC, ServerUrl);
        await c.Setup.UnlockWithRecoveryKeyAsync(recovery);
        Assert.Equal(new Note("Plan", "from A"), c.Notes.Get(id));

        // A sees three devices and revokes B; B is locked out but keeps its local copy.
        Assert.Equal(["PC nhà", "Laptop", "Phone"], (await a.Setup.ListDevicesAsync()).Select(d => d.Name));
        await a.Setup.RevokeDeviceAsync(deviceB.Id);
        b.Notes.Add(new Note("after revoke", "x"));
        Assert.Equal(SyncRunOutcome.Unauthorized, (await b.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(new Note("Plan", "from A"), b.Notes.Get(id));

        // C signs out and revokes its own token.
        await c.Setup.SignOutAsync(revokeToken: true);
        Assert.Equal(SyncSetupStage.NotConnected, c.Setup.Stage);
        Assert.NotNull((await a.Setup.ListDevicesAsync()).Single(d => d.Name == "Phone").RevokedAt);
    }

    [ServerFact]
    public async Task A_full_quota_stops_uploads_and_keeps_local_edits()
    {
        var a = NewSetupDevice("a");
        await a.Setup.RedeemInviteAsync(await CreateInviteAsync(quotaMb: 1), "Tiny", "PC", ServerUrl);
        await a.Setup.CreatePassphraseAsync("a long enough passphrase");
        for (var i = 0; i < 12; i++) a.Notes.Upsert($"big{i}", new Note("big", new string('x', 100_000)));

        var result = await a.Engine.SyncNowAsync();

        Assert.Equal(SyncRunOutcome.QuotaExceeded, result.Outcome);
        Assert.Equal(SyncState.QuotaExceeded, a.Engine.Status.State);
        Assert.Equal(12, a.Notes.All().Count);
        var usage = await a.Setup.GetAccountAsync();
        Assert.True(usage.UsedBytes <= usage.QuotaBytes);
    }

    [ServerFact]
    public async Task Moving_a_device_to_another_account_uploads_its_data_there()
    {
        var a = NewSetupDevice("a");
        await a.Setup.RedeemInviteAsync(await CreateInviteAsync(), "First", "PC", ServerUrl);
        await a.Setup.CreatePassphraseAsync("first account passphrase");
        var id = a.Notes.Add(new Note("Keep me", "moves with the device"));
        await a.Engine.SyncNowAsync();

        await a.Setup.SignOutAsync(revokeToken: false);
        await a.Setup.RedeemInviteAsync(await CreateInviteAsync(), "Second", "PC", ServerUrl);
        Assert.Equal(SyncSetupStage.NeedsKey, a.Setup.Stage);
        await a.Setup.CreatePassphraseAsync("second account passphrase");

        var (_, token) = await a.Setup.AddDeviceAsync("Laptop");
        var b = NewSetupDevice("b");
        await b.Setup.ConnectWithTokenAsync(token, ServerUrl);
        await b.Setup.UnlockWithPassphraseAsync("second account passphrase");
        Assert.Equal(new Note("Keep me", "moves with the device"), b.Notes.Get(id));
    }

    private sealed record Note(string Title, string Body);

    private sealed record Device(SyncEngine Engine, SyncedCollection<Note> Notes);

    private sealed record SetupDevice(SyncSetupService Setup, SyncEngine Engine, SyncedCollection<Note> Notes);

    private SetupDevice NewSetupDevice(string name)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db")));
        var credentials = new InMemorySyncCredentialStore();
        var keys = new InMemoryMasterKeyStore();
        var api = Own(new SyncApiClient());
        var engine = Own(new SyncEngine(db, new HttpSyncTransport(credentials, api), keys, [], debounce: TimeSpan.FromHours(1)));
        var notes = Own(new SyncedCollection<Note>(engine, new() { Name = "notes", ConflictPolicy = SyncConflictPolicy.KeepBoth }));
        return new SetupDevice(new SyncSetupService(credentials, keys, api, engine), engine, notes);
    }

    private async Task<string> CreateInviteAsync(int quotaMb = 10)
    {
        var body = await AdminAsync(HttpMethod.Post, "admin/invites", new { note = "test", quotaMb });
        return body.GetProperty("code").GetString()!;
    }

    private Device NewDevice(string name, string token)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db")));
        var transport = new HttpSyncTransport(new InMemorySyncCredentialStore(new SyncCredentials(ServerUrl!, token)), Own(new SyncApiClient()));
        var engine = Own(new SyncEngine(db, transport, new InMemoryMasterKeyStore(_key), [], debounce: TimeSpan.FromHours(1)));
        var notes = Own(new SyncedCollection<Note>(engine, new()
        {
            Name = "notes",
            ConflictPolicy = SyncConflictPolicy.KeepBoth,
            CreateConflictCopy = n => n with { Title = n.Title + " (conflict)" },
        }));
        return new Device(engine, notes);
    }

    private static async Task SyncAll(params Device[] devices)
    {
        foreach (var device in devices) Assert.Equal(SyncRunOutcome.Completed, (await device.Engine.SyncNowAsync()).Outcome);
    }

    private async Task<string> CreateAccountAsync(string name = "test")
    {
        var body = await AdminAsync(HttpMethod.Post, "admin/accounts", new { name });
        return body.GetProperty("accountId").GetString()!;
    }

    private async Task<string> CreateTokenAsync(string accountId, string name, string[]? scopes = null) =>
        (await CreateTokenWithIdAsync(accountId, name, scopes)).Token;

    private async Task<(string Token, string Id)> CreateTokenWithIdAsync(string accountId, string name, string[]? scopes = null)
    {
        object request = scopes is null ? new { name } : new { name, scopes };
        var body = await AdminAsync(HttpMethod.Post, $"admin/accounts/{accountId}/tokens", request);
        return (body.GetProperty("token").GetString()!, body.GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> AdminAsync(HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, new Uri(ServerUrl!, path)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await _admin.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string ForgeToken(string accountId)
    {
        var body = $"{SyncToken.Prefix}{accountId}_{new string('Z', 40)}";
        return body + SyncToken.Checksum(body);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }
}
