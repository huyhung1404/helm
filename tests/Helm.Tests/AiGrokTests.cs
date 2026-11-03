using System.Net;
using System.Net.Http;
using Helm.Ai;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>Grok on the AI page: what xAI tells about a key, the team's prepaid balance, and the management key that reads it.</summary>
public sealed class AiGrokTests : IDisposable
{
    private const string Key = "xai-AbCdEfGhIjKlMnOpQrStUvWxYz0123b2Zz";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_working_key_tells_its_name_team_and_models()
    {
        var http = new Fake(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/api-key" => (200, """{"name":"Helm","team_id":"team-1","api_key_blocked":false,"api_key_disabled":false,"team_blocked":false}"""),
            "/v1/models" => (200, """{"object":"list","data":[{"id":"grok-4.7","object":"model"},{"id":"grok-4.3","object":"model"}]}"""),
            _ => (404, ""),
        });

        var check = await new GrokKeyChecker(new HttpClient(http)).CheckAsync(" " + Key + " ", CancellationToken.None);

        Assert.Equal(AiKeyStatus.Works, check.Status);
        Assert.Equal("Helm", check.RemoteName);
        Assert.Equal("team-1", check.Account);
        Assert.Equal(["grok-4.7", "grok-4.3"], check.Models.Select(m => m.Id));
        Assert.All(http.Requests, r => Assert.Equal("Bearer " + Key, r.Headers.Authorization!.ToString()));
    }

    [Theory]
    [InlineData("""{"api_key_disabled":true}""", "This key is turned off in the xAI Console.")]
    [InlineData("""{"api_key_blocked":true}""", "xAI has blocked this key.")]
    [InlineData("""{"team_blocked":true}""", "xAI has blocked this key's team (often: no credit left, or a payment problem).")]
    public async Task A_blocked_key_or_team_is_said(string info, string message)
    {
        var http = new Fake(r => r.RequestUri!.AbsolutePath == "/v1/api-key" ? (200, info) : (200, """{"data":[]}"""));

        var check = await new GrokKeyChecker(new HttpClient(http)).CheckAsync(Key, CancellationToken.None);

        Assert.Equal(AiKeyStatus.Blocked, check.Status);
        Assert.Equal(message, check.Message);
    }

    [Fact]
    public async Task A_wrong_key_is_rejected()
    {
        var http = new Fake(_ => (400, """{"error":"Incorrect API key provided"}"""));

        var check = await new GrokKeyChecker(new HttpClient(http)).CheckAsync(Key, CancellationToken.None);

        Assert.Equal(AiKeyStatus.Rejected, check.Status);
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData("-1250", 12.50)]
    [InlineData("0", 0)]
    [InlineData("300", -3)]
    public async Task The_balance_is_the_opposite_of_the_ledger_in_cents(string total, double usd)
    {
        var http = new Fake(_ => (200, """{"changes":[],"total":{"val":"VAL"}}""".Replace("VAL", total)));

        var balance = await new GrokBalanceReader(new HttpClient(http)).ReadAsync("mgmt", "team 1", CancellationToken.None);

        Assert.Equal((decimal)usd, balance.Usd);
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://management-api.x.ai/v1/billing/teams/team%201/prepaid/balance", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer mgmt", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_refused_management_key_reads_nothing()
    {
        var http = new Fake(_ => (403, """{"error":"forbidden"}"""));

        var balance = await new GrokBalanceReader(new HttpClient(http)).ReadAsync("mgmt", "team-1", CancellationToken.None);

        Assert.Null(balance.Usd);
        Assert.Contains("management key", balance.Message);
    }

    [Fact]
    public async Task The_management_key_is_kept_only_when_it_reads_the_balance()
    {
        var records = new MemorySynced<AiKey>();
        var store = new AiKeyStore(records);
        store.Add(AiProviders.Grok, "Helm", Key, new AiKeyCheck(DateTimeOffset.UnixEpoch, AiKeyStatus.Works, "Works.", []) { Account = "team-1" });
        var management = new AiManagementKeys(new HelmPaths(_dir.Path), new PlainSecretProtector());
        var reader = new Reader();
        var vm = new AiKeysViewModel(store, management, [], [reader], new InlineUi(), new Launcher(), new AcceptDialogs());
        var grok = vm.Sections.Single(s => s.Provider == AiProviders.Grok);
        Assert.True(grok.ReadsBalance);
        Assert.False(vm.Sections.Single(s => s.Provider == AiProviders.Claude).ReadsBalance);

        vm.StartManagementKeyCommand.Execute(grok);
        grok.NewManagementKey = "wrong";
        await vm.SaveManagementKeyCommand.ExecuteAsync(grok);
        Assert.False(management.Has(AiProviders.Grok));
        Assert.Equal("refused", grok.ManagementProblem);

        grok.NewManagementKey = "right";
        await vm.SaveManagementKeyCommand.ExecuteAsync(grok);
        Assert.Equal("right", management.Get(AiProviders.Grok));
        Assert.Equal("team-1", reader.LastAccount);
        Assert.True(grok.HasManagementKey);
        Assert.False(grok.IsAddingManagementKey);
        Assert.Equal("$1.50", grok.BalanceText);
        Assert.True(grok.IsLowBalance);

        await vm.RemoveManagementKeyCommand.ExecuteAsync(grok);
        Assert.False(management.Has(AiProviders.Grok));
        Assert.Equal("", grok.BalanceText);
    }

    [Fact]
    public async Task Without_a_tested_key_there_is_no_team_to_read()
    {
        var store = new AiKeyStore(new MemorySynced<AiKey>());
        var vm = new AiKeysViewModel(store, new AiManagementKeys(new HelmPaths(_dir.Path), new PlainSecretProtector()), [], [new Reader()],
            new InlineUi(), new Launcher(), new AcceptDialogs());
        var grok = vm.Sections.Single(s => s.Provider == AiProviders.Grok);

        grok.NewManagementKey = "right";
        await vm.SaveManagementKeyCommand.ExecuteAsync(grok);

        Assert.Equal("Add and test a Grok key first: Helm reads the balance of its team.", grok.ManagementProblem);
    }

    private sealed class Reader : IAiBalanceReader
    {
        public string? LastAccount { get; private set; }

        public AiProvider Provider => AiProviders.Grok;

        public Task<AiBalance> ReadAsync(string managementKey, string account, CancellationToken ct)
        {
            LastAccount = account;
            return Task.FromResult(managementKey == "right"
                ? new AiBalance(DateTimeOffset.UnixEpoch, 1.5m, "Prepaid credit left.")
                : new AiBalance(DateTimeOffset.UnixEpoch, null, "refused"));
        }
    }

    private sealed class Fake(Func<HttpRequestMessage, (int Status, string Body)> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }

    private sealed class Launcher : Helm.Core.Services.IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }
}
