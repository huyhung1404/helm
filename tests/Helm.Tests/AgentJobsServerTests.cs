using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>
/// End-to-end against the real sync Worker: a phone's Ask Helm turn reaches a PC's agent through Helm Sync (urgent
/// records, the live channel, end-to-end encryption) and the answer comes back. Runs when HELM_SYNC_TEST_URL is set.
/// </summary>
public sealed class AgentJobsServerTests : IDisposable
{
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
    public async Task A_phone_turn_runs_on_the_PC_through_the_worker_and_its_answer_comes_back()
    {
        var account = await AdminAsync(HttpMethod.Post, "admin/accounts", new { name = "ask-helm" });
        var accountId = account.GetProperty("accountId").GetString()!;
        var phone = Device("phone", await TokenAsync(accountId, "Phone"));
        var pc = Device("pc", await TokenAsync(accountId, "PC"));
        phone.Engine.SetLive(true);
        pc.Engine.SetLive(true);
        await WaitUntil(() => phone.Engine.IsLive && pc.Engine.IsLive, "both live channels connect");

        var local = new Local(new AiJobResult("Em ghi rồi nha Anh.", [new AiJobAction("wallet_add_transaction", "Chi 45.000đ · Phở", """{"id":"t1"}""")], [], 0.001m));
        using var worker = new AgentJobWorker(pc.Jobs, pc.Workers, local, () => pc.Engine.DeviceId, "PC-HUNG", () => ["Codex"]);
        worker.Start();
        var route = new SyncAgentRoute(phone.Jobs, phone.Workers, () => phone.Engine.DeviceId, phone.Engine);
        await WaitUntil(() => route.Unavailable() is null, "the phone hears the PC's heartbeat");
        Assert.Equal("PC-HUNG", route.Device);

        var took = Stopwatch.StartNew();
        var result = await route.RunAsync(new AiJob(AiFeatureIds.AskHelm, "conv", "phở 45k", ["wallet_add_transaction"], [], "claude-haiku-5-5", "low"),
            TimeSpan.FromSeconds(8), CancellationToken.None);
        took.Stop();

        Assert.Equal("Em ghi rồi nha Anh.", result.Text);
        Assert.Equal("phở 45k", Assert.Single(local.Jobs).Text);
        // Through the real Worker and both live channels, with a fake agent: the sync part of the time.
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(8), $"The round trip took {took.Elapsed.TotalSeconds:0.0} s.");

        await route.UndoAsync(result.Done[0], CancellationToken.None);
        Assert.Single(local.Undone);
        phone.Engine.SetLive(false);
        pc.Engine.SetLive(false);
    }

    private sealed record DeviceParts(SyncEngine Engine, ISyncedCollection<AiJobRecord> Jobs, ISyncedCollection<AiWorkerRecord> Workers);

    private DeviceParts Device(string name, string token)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var transport = new HttpSyncTransport(new InMemorySyncCredentialStore(new SyncCredentials(SyncServerTests.ServerUrl!, token)), Own(new SyncApiClient()));
        var engine = Own(new SyncEngine(db, transport, new InMemoryMasterKeyStore(_key), [], debounce: TimeSpan.FromMilliseconds(50)));
        var jobs = Own(new SyncedCollection<AiJobRecord>(engine, new() { Name = AgentJobs.JobsCollection, Urgent = true }));
        var workers = Own(new SyncedCollection<AiWorkerRecord>(engine, new() { Name = AgentJobs.WorkersCollection, Urgent = true }));
        return new DeviceParts(engine, jobs, workers);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private async Task<string> TokenAsync(string accountId, string name) =>
        (await AdminAsync(HttpMethod.Post, $"admin/accounts/{accountId}/tokens", new { name })).GetProperty("token").GetString()!;

    private async Task<JsonElement> AdminAsync(HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, new Uri(SyncServerTests.ServerUrl!, path)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SyncServerTests.AdminToken);
        using var response = await _admin.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(100);
        }
    }

    private sealed class Local(AiJobResult result) : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public List<AiJobAction> Undone { get; } = [];
        public string Device => "PC-HUNG";
        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            return Task.FromResult(result);
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct)
        {
            lock (Undone) Undone.Add(action);
            return Task.CompletedTask;
        }

        public void End(string conversationId) { }
    }
}
