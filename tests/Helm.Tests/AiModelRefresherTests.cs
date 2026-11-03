using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Services;

namespace Helm.Tests;

/// <summary>
/// The model choice keeps itself up to date: keys are tested again once a day (a test without an answer keeps the last
/// one), only chat models are offered, and a pick the provider retired falls back to its default.
/// </summary>
public sealed class AiModelRefresherTests
{
    private const string Claude = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";
    private const string Grok = "xai-AbCdEfGhIjKlMnOpQrStUvWxYz0123b2Zz";

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
    private readonly MemorySynced<AiKey> _records = new();
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly FakeChecker _claude = new(AiProviders.Claude);
    private readonly FakeChecker _grok = new(AiProviders.Grok);
    private readonly AiKeyStore _keys;

    public AiModelRefresherTests() => _keys = new AiKeyStore(_records, _time);

    private AiModelRefresher Refresher() => new(_keys, [_claude, _grok], _features, time: _time);

    private AiKeyCheck Check(DateTimeOffset at, params string[] models) =>
        new(at, AiKeyStatus.Works, "Works.", [.. models.Select(m => new AiModel(m, m))]);

    [Theory]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("grok-4.7", true)]
    [InlineData("grok-2-vision-1212", true)]
    [InlineData("grok-code-fast-1", true)]
    [InlineData("grok-imagine-image", false)]
    [InlineData("grok-imagine-video", false)]
    [InlineData("grok-2-image-1212", false)]
    [InlineData("text-embedding-3", false)]
    [InlineData("claude-tts-1", false)]
    [InlineData("voyage-embed-2", false)]
    public void Only_chat_models_are_offered(string id, bool chat) => Assert.Equal(chat, AiModels.IsChat(id));

    [Fact]
    public async Task A_key_is_tested_again_once_its_list_is_a_day_old()
    {
        var id = _keys.Add(AiProviders.Claude, "Helm", Claude, Check(_time.Now.AddHours(-2), "claude-haiku-5-5"));
        _claude.Next = Check(_time.Now, "claude-haiku-5-5", "claude-opus-5-5");
        var refresher = Refresher();

        await refresher.RefreshDueAsync(CancellationToken.None);
        Assert.Equal(0, _claude.Calls);

        _time.Advance(TimeSpan.FromHours(22));
        _claude.Next = Check(_time.Now, "claude-haiku-5-5", "claude-opus-5-5");
        await refresher.RefreshDueAsync(CancellationToken.None);

        Assert.Equal(1, _claude.Calls);
        Assert.Equal(2, _keys.Get(id)!.LastCheck!.Models.Count);

        // Tested just now: not again until tomorrow.
        _time.Advance(TimeSpan.FromHours(1));
        await refresher.RefreshDueAsync(CancellationToken.None);
        Assert.Equal(1, _claude.Calls);
    }

    [Fact]
    public async Task A_key_never_tested_is_tested_at_once()
    {
        var id = _keys.Add(AiProviders.Grok, "Grok", Grok);
        _grok.Next = Check(_time.Now, "grok-4.7");

        await Refresher().RefreshDueAsync(CancellationToken.None);

        Assert.Equal("grok-4.7", _keys.Get(id)!.LastCheck!.Models.Single().Id);
    }

    [Fact]
    public async Task A_test_without_an_answer_keeps_the_last_list()
    {
        var id = _keys.Add(AiProviders.Claude, "Helm", Claude, Check(_time.Now.AddDays(-2), "claude-haiku-5-5"));
        _claude.Next = new AiKeyCheck(_time.Now, AiKeyStatus.Unknown, "Could not reach Claude.", []);

        await Refresher().RefreshDueAsync(CancellationToken.None);

        Assert.Equal(1, _claude.Calls);
        Assert.Equal(AiKeyStatus.Works, _keys.Get(id)!.LastCheck!.Status);
        Assert.Single(_keys.Get(id)!.LastCheck!.Models);
    }

    [Fact]
    public async Task A_pick_the_provider_retired_falls_back_to_its_default()
    {
        _keys.Add(AiProviders.Claude, "Helm", Claude, Check(_time.Now.AddDays(-2), "claude-haiku-5-5", "claude-sonnet-4-6"));
        Pick(AiFeatureIds.AskHelm, AiProviders.Claude, "claude-sonnet-4-6");
        _claude.Next = Check(_time.Now, "claude-haiku-5-5", "claude-sonnet-5-5");

        await Refresher().RefreshDueAsync(CancellationToken.None);

        var choice = _features.Current.For(AiFeatureIds.AskHelm);
        Assert.Equal(ClaudeModels.Haiku, choice.Model);
        Assert.Equal(AiProviders.Claude.Id, choice.Provider);
    }

    [Fact]
    public void Without_the_default_either_the_first_chat_model_is_picked()
    {
        _keys.Add(AiProviders.Grok, "Grok", Grok, Check(_time.Now, "grok-imagine-image", "grok-5", "grok-5-mini"));
        Pick("novel", AiProviders.Grok, "grok-4.7");

        Refresher().KeepPicksOffered();

        Assert.Equal("grok-5", _features.Current.For("novel").Model);
    }

    [Fact]
    public void A_pick_still_offered_or_a_key_without_a_list_changes_nothing()
    {
        _keys.Add(AiProviders.Claude, "Helm", Claude, Check(_time.Now, "claude-haiku-5-5", "claude-opus-5-5"));
        _keys.Add(AiProviders.Grok, "Grok", Grok, new AiKeyCheck(_time.Now, AiKeyStatus.Rejected, "No.", []));
        Pick(AiFeatureIds.AskHelm, AiProviders.Claude, "claude-opus-5-5");
        Pick("novel", AiProviders.Grok, "grok-old");

        Refresher().KeepPicksOffered();

        Assert.Equal("claude-opus-5-5", _features.Current.For(AiFeatureIds.AskHelm).Model);
        Assert.Equal("grok-old", _features.Current.For("novel").Model);
    }

    [Fact]
    public void Ask_Helm_lists_chat_models_only()
    {
        _keys.Add(AiProviders.Claude, "Helm", Claude, Check(_time.Now, "claude-haiku-5-5", "claude-embed-1"));
        _keys.Add(AiProviders.Grok, "Grok", Grok, Check(_time.Now, "grok-4.7", "grok-imagine-video", "grok-2-image-1212"));
        var assistant = new HelmAssistant(_keys, _features, new AiUsageBook(new MemorySyncedLog<AiUsageEntry>()), [],
            [new Chats(AiProviders.Claude), new Chats(AiProviders.Grok)], _ => true);

        Assert.Equal(["claude-haiku-5-5", "grok-4.7"], assistant.Models().Select(m => m.Model));
    }

    [Fact]
    public void The_page_tells_when_the_model_lists_were_updated()
    {
        _keys.Add(AiProviders.Claude, "Helm", Claude, Check(new DateTimeOffset(2026, 10, 30, 7, 5, 0, TimeSpan.Zero), "claude-haiku-5-5"));
        _keys.Add(AiProviders.Grok, "Grok", Grok, Check(new DateTimeOffset(2026, 11, 1, 7, 5, 0, TimeSpan.Zero), "grok-4.7"));
        var vm = new AiKeysViewModel(_keys, new AiManagementKeys(new MemorySynced<AiManagementKey>()), [], [], new InlineUi(),
            new NullLauncher(), new AcceptDialogs());

        // The oldest of the lists.
        Assert.Equal($"Model lists updated {AiKeysViewModel.When(new DateTimeOffset(2026, 10, 30, 7, 5, 0, TimeSpan.Zero))}", vm.ModelsUpdatedText);
    }

    private void Pick(string feature, AiProvider provider, string model) => _features.Update(s =>
        s.Features[feature] = new AiFeatureChoice { Provider = provider.Id, Model = model });

    private sealed class FakeChecker(AiProvider provider) : IAiKeyChecker
    {
        public AiKeyCheck? Next { get; set; }
        public int Calls { get; private set; }

        public AiProvider Provider => provider;

        public Task<AiKeyCheck> CheckAsync(string secret, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next ?? throw new InvalidOperationException("No answer set."));
        }
    }

    private sealed class Chats(AiProvider provider) : IAssistantChatFactory
    {
        public AiProvider Provider => provider;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools) =>
            throw new NotSupportedException();
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
