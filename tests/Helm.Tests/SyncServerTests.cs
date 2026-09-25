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

    [ServerFact]
    public async Task Removing_a_device_with_key_rotation_locks_it_out_of_new_data_and_others_unlock_once()
    {
        const string pass = "a long enough rotation passphrase";
        var a = NewSetupDevice("a");
        await a.Setup.RedeemInviteAsync(await CreateInviteAsync(), "Rotate", "PC", ServerUrl);
        var oldRecovery = await a.Setup.CreatePassphraseAsync(pass);
        var before = a.Notes.Add(new Note("Before", "epoch 1"));
        await a.Engine.SyncNowAsync();

        var (_, tokenB) = await a.Setup.AddDeviceAsync("Laptop");
        var (lost, tokenLost) = await a.Setup.AddDeviceAsync("Lost phone");
        var b = NewSetupDevice("b");
        await b.Setup.ConnectWithTokenAsync(tokenB, ServerUrl);
        await b.Setup.UnlockWithPassphraseAsync(pass);
        var thief = NewSetupDevice("thief");
        await thief.Setup.ConnectWithTokenAsync(tokenLost, ServerUrl);
        await thief.Setup.UnlockWithPassphraseAsync(pass);

        await Assert.ThrowsAsync<SyncKeyException>(() => a.Setup.RemoveDeviceAndRotateKeyAsync(lost.Id, "wrong passphrase!!"));
        var newRecovery = await a.Setup.RemoveDeviceAndRotateKeyAsync(lost.Id, pass);
        Assert.NotEqual(oldRecovery, newRecovery);
        var after = a.Notes.Add(new Note("After", "epoch 2"));
        Assert.Equal(SyncRunOutcome.Completed, (await a.Engine.SyncNowAsync()).Outcome);

        // The removed device is refused by the server.
        Assert.Equal(SyncRunOutcome.Unauthorized, (await thief.Engine.SyncNowAsync()).Outcome);

        // B notices the new key, keeps its data, and continues after one unlock.
        Assert.Equal(SyncRunOutcome.KeyChanged, (await b.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(SyncSetupStage.NeedsKey, b.Setup.Stage);
        Assert.True(b.Setup.KeyChangedElsewhere);
        Assert.Equal(new Note("Before", "epoch 1"), b.Notes.Get(before));
        await b.Setup.UnlockWithPassphraseAsync(pass);
        Assert.Equal(SyncSetupStage.Ready, b.Setup.Stage);
        Assert.Equal(new Note("After", "epoch 2"), b.Notes.Get(after));

        // The old recovery key no longer opens the account; the new one does, and sees both epochs.
        var (_, tokenD) = await a.Setup.AddDeviceAsync("New PC");
        var d = NewSetupDevice("d");
        await d.Setup.ConnectWithTokenAsync(tokenD, ServerUrl);
        await Assert.ThrowsAsync<SyncKeyException>(() => d.Setup.UnlockWithRecoveryKeyAsync(oldRecovery));
        await d.Setup.UnlockWithRecoveryKeyAsync(newRecovery);
        Assert.Equal(new Note("Before", "epoch 1"), d.Notes.Get(before));
        Assert.Equal(new Note("After", "epoch 2"), d.Notes.Get(after));
    }

    [ServerFact]
    public async Task A_helm_0_5_keyring_is_upgraded_to_argon2id_when_a_device_unlocks()
    {
        var a = NewSetupDevice("a");
        await a.Setup.RedeemInviteAsync(await CreateInviteAsync(), "Legacy", "PC", ServerUrl);
        var legacy = LegacyKeyring("the old 0.5 passphrase");
        using (var api = new SyncApiClient())
            Assert.True(await api.PutKeyringAsync(a.Credentials.Load()!, 0, legacy));
        Assert.True(await a.Setup.NeedsProtectionUpgradeAsync());

        await a.Setup.UnlockWithPassphraseAsync("the old 0.5 passphrase");

        Assert.Equal(SyncSetupStage.Ready, a.Setup.Stage);
        Assert.False(await a.Setup.NeedsProtectionUpgradeAsync());
    }

    [ServerFact]
    public async Task A_backup_can_be_restored_and_devices_receive_it()
    {
        var a = NewSetupDevice("a");
        var account = await a.Setup.RedeemInviteAsync(await CreateInviteAsync(), "Backup", "PC", ServerUrl);
        await a.Setup.CreatePassphraseAsync("a long enough backup passphrase");
        var id = a.Notes.Add(new Note("Plan", "good version"));
        await a.Engine.SyncNowAsync();

        var run = await AdminAsync(HttpMethod.Post, "admin/backups/run", new { });
        Assert.True(run.GetProperty("accounts").GetInt32() >= 1);
        var list = await AdminGetAsync($"admin/backups?prefix=accounts/{account.AccountId}/");
        var key = list.GetProperty("backups")[0].GetProperty("key").GetString()!;

        a.Notes.Upsert(id, new Note("Plan", "accidentally overwritten"));
        await a.Engine.SyncNowAsync();

        using (var wrongConfirm = new HttpRequestMessage(HttpMethod.Post, new Uri(ServerUrl!, $"admin/accounts/{account.AccountId}/restore"))
               { Content = JsonContent.Create(new { key, confirm = "nope" }) })
        {
            wrongConfirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _admin.SendAsync(wrongConfirm)).StatusCode);
        }
        var restored = await AdminAsync(HttpMethod.Post, $"admin/accounts/{account.AccountId}/restore", new { key, confirm = account.AccountId });
        Assert.Equal(1, restored.GetProperty("restored").GetInt32());

        Assert.Equal(SyncRunOutcome.Completed, (await a.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(new Note("Plan", "good version"), a.Notes.Get(id));
    }

    [ServerFact]
    public async Task Invite_redemption_is_rate_limited_per_client()
    {
        using var client = new HttpClient();
        // Its own client address, so this test does not use up the limit of the other tests (all from 127.0.0.1).
        // Cloudflare overwrites CF-Connecting-IP with the real address in production, so clients cannot pick it.
        var ip = $"198.51.100.{Random.Shared.Next(1, 255)}";
        var statuses = new List<int>();
        for (var i = 0; i < 25; i++)
        {
            var body = SyncToken.InvitePrefix + new string('Q', 40);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ServerUrl!, "v1/redeem"))
            {
                Content = JsonContent.Create(new { invite = body + SyncToken.Checksum(body), accountName = "x", deviceName = "y" }),
            };
            request.Headers.Add("CF-Connecting-IP", ip);
            statuses.Add((int)(await client.SendAsync(request)).StatusCode);
        }
        Assert.Contains(429, statuses);
        Assert.All(statuses, s => Assert.True(s is 400 or 429, $"unexpected {s}"));
    }

    private sealed record Note(string Title, string Body);

    private sealed record Device(SyncEngine Engine, SyncedCollection<Note> Notes);

    private async Task<JsonElement> AdminGetAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ServerUrl!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await _admin.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A keyring as Helm 0.5.0 wrote it (v1, PBKDF2; 100k iterations to keep the test fast).</summary>
    private static string LegacyKeyring(string passphrase)
    {
        var master = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var kek = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(passphrase), salt, 100_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var recoveryKek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return JsonSerializer.Serialize(new
        {
            v = 1,
            kdf = new { alg = "pbkdf2-sha256", iter = 100_000, salt },
            pass = WrapKey(kek, master, "helm-sync/v1/keyring|passphrase"),
            rec = WrapKey(recoveryKek, master, "helm-sync/v1/keyring|recovery"),
        });
    }

    private static byte[] WrapKey(byte[] kek, byte[] master, string aad)
    {
        var output = new byte[60];
        System.Security.Cryptography.RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var gcm = new System.Security.Cryptography.AesGcm(kek, 16);
        gcm.Encrypt(output.AsSpan(0, 12), master, output.AsSpan(28), output.AsSpan(12, 16), System.Text.Encoding.UTF8.GetBytes(aad));
        return output;
    }

    private sealed record SetupDevice(SyncSetupService Setup, SyncEngine Engine, SyncedCollection<Note> Notes, ISyncCredentialStore Credentials);

    private SetupDevice NewSetupDevice(string name)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var credentials = new InMemorySyncCredentialStore();
        var keys = new InMemoryMasterKeyStore();
        var api = Own(new SyncApiClient());
        var engine = Own(new SyncEngine(db, new HttpSyncTransport(credentials, api), keys, [], debounce: TimeSpan.FromHours(1)));
        var notes = Own(new SyncedCollection<Note>(engine, new() { Name = "notes", ConflictPolicy = SyncConflictPolicy.KeepBoth }));
        return new SetupDevice(new SyncSetupService(credentials, keys, api, engine), engine, notes, credentials);
    }

    private async Task<string> CreateInviteAsync(int quotaMb = 10)
    {
        var body = await AdminAsync(HttpMethod.Post, "admin/invites", new { note = "test", quotaMb });
        return body.GetProperty("code").GetString()!;
    }

    private Device NewDevice(string name, string token)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
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
