using Helm.Ai;
using Helm.Core.Services;

namespace Helm.Tests;

/// <summary>The API keys of the AI page: the store (on Helm Sync), the default per provider, and the page's add flow.</summary>
public sealed class AiKeysTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const string Good = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";
    private const string Other = "sk-ant-api03-zyxwvutsrqponmlkjihgfedcba9876-b2Zz";

    private readonly MemorySynced<AiKey> _records = new();

    [Theory]
    [InlineData("", "Paste the key.")]
    [InlineData("sk-ant-api03-abc def", "A key has no spaces or line breaks: copy it again.")]
    [InlineData("xai-abcdefghijklmnopqrstuvwxyz", "A Claude key starts with sk-ant-")]
    [InlineData("sk-ant-abc", "This key is too short: copy all of it.")]
    [InlineData("  " + Good + "\n", null)]
    public void Problem_explains_a_key_that_cannot_be_right(string secret, string? problem) =>
        Assert.Equal(problem, AiKeyStore.Problem(AiProviders.Claude, secret));

    [Fact]
    public void Mask_shows_only_the_prefix_and_the_last_four()
    {
        Assert.Equal("sk-ant-…x7Qa", AiKeyStore.Mask(Good));
        Assert.Equal("xai-…b2Zz", AiKeyStore.Mask("xai-AbCdEfGhIjKlMnOpQrStUvWxYz0123b2Zz"));
        Assert.Equal("••••", AiKeyStore.Mask("abcd"));
    }

    [Fact]
    public void The_first_key_of_a_provider_is_its_default()
    {
        var store = new AiKeyStore(_records);
        var first = store.Add(AiProviders.Claude, "  Helm   phone ", " " + Good + " ");
        var second = store.Add(AiProviders.Claude, "", Other);

        Assert.True(store.Get(first)!.IsDefault);
        Assert.False(store.Get(second)!.IsDefault);
        Assert.Equal("Helm phone", store.Get(first)!.Label);
        Assert.Equal(Good, store.Get(first)!.Secret);
        Assert.Equal("Claude", store.Get(second)!.Label);
        Assert.Equal(first, store.DefaultFor(AiProviders.Claude)!.Id);
        Assert.True(store.Contains(Good + "\n"));
    }

    [Fact]
    public void Add_refuses_a_key_that_cannot_be_right()
    {
        var store = new AiKeyStore(_records);
        Assert.Throws<ArgumentException>(() => store.Add(AiProviders.Claude, "Helm", "sk-proj-123"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void MakeDefault_moves_the_flag_and_Delete_hands_it_on()
    {
        var store = new AiKeyStore(_records);
        var first = store.Add(AiProviders.Claude, "PC", Good);
        var second = store.Add(AiProviders.Claude, "Phone", Other);

        store.MakeDefault(second);
        Assert.False(store.Get(first)!.IsDefault);
        Assert.Equal(second, store.DefaultFor(AiProviders.Claude)!.Id);

        Assert.True(store.Delete(second));
        Assert.True(store.Get(first)!.IsDefault);
        Assert.False(store.Delete(second));
    }

    [Fact]
    public void Two_defaults_from_two_devices_resolve_to_one()
    {
        var store = new AiKeyStore(_records);
        var first = store.Add(AiProviders.Claude, "PC", Good);
        var second = store.Add(AiProviders.Claude, "Phone", Other);
        _records.SetRemote(second, store.Get(second)! with { IsDefault = true });

        Assert.NotNull(store.DefaultFor(AiProviders.Claude));
        Assert.Contains(store.DefaultFor(AiProviders.Claude)!.Id, new[] { first, second });
    }

    [Fact]
    public async Task A_rejected_key_is_not_saved()
    {
        var checker = new FakeChecker(AiKeyStatus.Rejected, "Claude does not accept this key.");
        var vm = ViewModel(checker, out var store);
        vm.StartAddCommand.Execute(AiProviders.Claude);
        vm.NewSecret = Good;

        await vm.SaveNewCommand.ExecuteAsync(null);

        Assert.Empty(store.All());
        Assert.True(vm.IsAdding);
        Assert.Equal("Claude does not accept this key.", vm.NewProblem);
    }

    [Fact]
    public async Task A_key_that_works_is_saved_with_its_test_and_listed()
    {
        var checker = new FakeChecker(AiKeyStatus.Works, "Works. 2 models.", new AiModel("claude-haiku-5-5", "Claude Haiku 5.5"),
            new AiModel("claude-sonnet-5-5", "Claude Sonnet 5.5"));
        var vm = ViewModel(checker, out var store);
        var cleared = false;
        vm.NewKeySaved += (_, _) => cleared = true;
        vm.StartAddCommand.Execute(AiProviders.Claude);
        vm.NewLabel = "Helm";
        vm.NewSecret = Good;

        await vm.SaveNewCommand.ExecuteAsync(null);

        var saved = Assert.Single(store.All());
        Assert.Equal(AiKeyStatus.Works, saved.Value.LastCheck!.Status);
        Assert.False(vm.IsAdding);
        Assert.Equal("", vm.NewSecret);
        Assert.True(cleared);
        var row = Assert.Single(vm.Sections[0].Keys);
        Assert.Equal("sk-ant-…x7Qa", row.Masked);
        Assert.True(row.IsDefault);
        Assert.True(row.Works);
        Assert.Equal("2 models", row.ModelsText);
        Assert.True(vm.Sections[0].HasKeys);
        Assert.False(vm.Sections[1].HasKeys);
    }

    [Fact]
    public async Task A_key_that_cannot_be_right_is_never_sent()
    {
        var checker = new FakeChecker(AiKeyStatus.Works, "Works.");
        var vm = ViewModel(checker, out _);
        vm.StartAddCommand.Execute(AiProviders.Claude);
        vm.NewSecret = "sk-proj-123";

        await vm.SaveNewCommand.ExecuteAsync(null);

        Assert.Equal(0, checker.Calls);
        Assert.Equal("A Claude key starts with sk-ant-", vm.NewProblem);
    }

    [Fact]
    public async Task Another_devices_key_shows_up_and_test_results_sync()
    {
        var checker = new FakeChecker(AiKeyStatus.Blocked, "No credit left.");
        var vm = ViewModel(checker, out var store);
        Assert.All(vm.Sections, s => Assert.False(s.HasKeys));

        _records.SetRemote("phone", new AiKey { Provider = "anthropic", Label = "Phone", Secret = Good, IsDefault = true });
        var row = Assert.Single(vm.Sections[0].Keys);
        Assert.True(row.IsUnknown);

        await vm.TestCommand.ExecuteAsync(row);

        Assert.True(vm.Sections[0].Keys[0].IsBlocked);
        Assert.Equal("No credit left.", store.Get("phone")!.LastCheck!.Message);
    }

    private AiKeysViewModel ViewModel(IAiKeyChecker checker, out AiKeyStore store, IAiBalanceReader? reader = null)
    {
        store = new AiKeyStore(_records);
        return new AiKeysViewModel(store, new AiManagementKeys(new Helm.Core.Settings.HelmPaths(_dir.Path), new Helm.Core.Sync.PlainSecretProtector()),
            [checker], reader is null ? [] : [reader], new InlineUi(), new NullLauncher(), new AcceptDialogs());
    }

    private sealed class FakeChecker(AiKeyStatus status, string message, params AiModel[] models) : IAiKeyChecker
    {
        public int Calls { get; private set; }

        public AiProvider Provider => AiProviders.Claude;

        public Task<AiKeyCheck> CheckAsync(string secret, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AiKeyCheck(DateTimeOffset.UnixEpoch, status, message, models));
        }
    }

    private sealed class NullLauncher : IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }
}
