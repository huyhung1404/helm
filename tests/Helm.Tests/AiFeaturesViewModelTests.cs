using System.Reflection;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Ai.Auto;

namespace Helm.Tests;

/// <summary>AI settings per feature (AI → Features on the PC, General › AI features on the phone).</summary>
public sealed class AiFeaturesViewModelTests
{
    private static readonly AssistantModelOption Haiku = new(AiProviders.Claude, ClaudeModels.Haiku, "Claude Haiku 5.5");
    private static readonly AssistantModelOption Sonnet = new(AiProviders.Claude, "claude-sonnet-5-5", "Claude Sonnet 5.5");
    private static readonly AssistantModelOption Grok = new(AiProviders.Grok, GrokModels.Default, "Grok 4.7");

    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();

    private AiFeaturesViewModel Build(params AssistantModelOption[] models) =>
        new(_features, new AiUsageBook(_usage), () => models.Length > 0 ? models : [Haiku, Sonnet, Grok], new InlineUi());

    private static AiFeatureRow Row(AiFeaturesViewModel vm, string id) => vm.Rows.Single(r => r.Id == id);

    [Fact]
    public void Every_feature_is_listed_once_on_request_or_automatic()
    {
        var vm = Build();
        var ids = typeof(AiFeatureIds).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!).ToList();

        Assert.Equal(ids.Order(), vm.Rows.Select(r => r.Id).Order());
        Assert.Equal(["On request", "Automatic"], vm.Groups.Select(g => g.Title));
        Assert.Contains(vm.Groups[0].Rows, r => r.Id == AiFeatureIds.AskHelm);
        Assert.All(AutoJobCatalog.All, job => Assert.Contains(vm.Groups[1].Rows, r => r.Id == job.Feature));
        Assert.Contains(vm.Groups[1].Rows, r => r.Id == AiFeatureIds.WalletCategorize);
        Assert.All(vm.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Fact]
    public void Automatic_features_start_on_agents_only_and_cost_nothing()
    {
        var vm = Build();

        Assert.All(vm.Groups[1].Rows, r => Assert.Equal(AiRoutePolicy.PcOnly, r.Route.Policy));
        Assert.All(vm.Groups[0].Rows, r => Assert.Equal(AiRoutePolicy.PreferPc, r.Route.Policy));
        var brief = Row(vm, AiFeatureIds.MorningBrief);
        Assert.Equal("Runs on your PC agents · 0đ", brief.CostText);
        Assert.False(brief.HasCostDetail);
        Assert.False(brief.CanReachApi);
        Assert.StartsWith("07:00 · ", brief.Description);
        Assert.Equal("all on your PC agents · 0đ", vm.Groups[1].Note);
        // Nothing was saved just by looking.
        Assert.Empty(_features.Current.Features);
    }

    [Fact]
    public void A_feature_without_its_own_choice_shows_Ask_Helms_model()
    {
        _features.Update(s => s.Features[AiFeatureIds.AskHelm] = new AiFeatureChoice { Model = Sonnet.Model, Enabled = false });

        var vm = Build();

        var text = Row(vm, AiFeatureIds.TextActions);
        Assert.Equal(Sonnet, text.Model);
        Assert.True(text.IsOn);
        Assert.Equal("Agents → API · Claude Sonnet 5.5", text.StateText);
        Assert.Equal(AiRoutePolicy.PcOnly, Row(vm, AiFeatureIds.WalletCategorize).Route.Policy);
        Assert.Equal("Off", Row(vm, AiFeatureIds.AskHelm).StateText);
    }

    [Fact]
    public void Effort_is_for_Claude_only()
    {
        var vm = Build();
        var row = Row(vm, AiFeatureIds.SentenceExplain);
        Assert.True(row.CanChooseEffort);

        row.Model = Grok;

        Assert.False(row.CanChooseEffort);
        var saved = _features.Current.Features[AiFeatureIds.SentenceExplain];
        Assert.Equal(AiProviders.Grok.Id, saved.Provider);
        Assert.Equal(GrokModels.Default, saved.Model);
    }

    [Fact]
    public void The_daily_cap_stays_within_its_limits()
    {
        var vm = Build();
        var row = Row(vm, AiFeatureIds.AskHelm);

        row.Cap = 50;
        Assert.Equal(AiFeatureRow.MaxCapUsd, row.Cap);
        Assert.Equal((decimal)AiFeatureRow.MaxCapUsd, _features.Current.For(AiFeatureIds.AskHelm).DailyCapUsd);

        row.Cap = -3;
        Assert.Equal(0, row.Cap);

        row.MoreCapCommand.Execute(null);
        row.MoreCapCommand.Execute(null);
        Assert.Equal(0.2, row.Cap);
        Assert.Equal(0.2m, _features.Current.For(AiFeatureIds.AskHelm).DailyCapUsd);
        row.LessCapCommand.Execute(null);
        row.LessCapCommand.Execute(null);
        row.LessCapCommand.Execute(null);
        Assert.Equal(0, row.Cap);
    }

    [Fact]
    public void A_change_saves_at_once_and_shows_on_the_other_device()
    {
        var pc = Build();
        var phone = Build();

        var row = Row(pc, AiFeatureIds.TextActions);
        row.Route = AiRouteOption.For(AiRoutePolicy.ApiOnly);
        row.Effort = AiEffortOption.For("high");

        var saved = _features.Current.Features[AiFeatureIds.TextActions];
        Assert.Equal(AiRoutePolicy.ApiOnly, saved.Route);
        Assert.Equal("high", saved.Effort);
        var there = Row(phone, AiFeatureIds.TextActions);
        Assert.Equal(AiRoutePolicy.ApiOnly, there.Route.Policy);
        Assert.Equal("high", there.Effort.Id);
        Assert.True(there.IsCostWarning);
        Assert.Equal("API · ≤ $0.50/day", there.CostText);
    }

    [Fact]
    public void Turning_a_job_off_keeps_its_time()
    {
        _features.Update(s => s.Features[AiFeatureIds.EveningReview] = new AiFeatureChoice { Route = AiRoutePolicy.PcOnly, At = "22:15" });
        var vm = Build();
        var row = Row(vm, AiFeatureIds.EveningReview);
        Assert.StartsWith("22:15 · ", row.Description);

        row.IsOn = false;

        var saved = _features.Current.For(AiFeatureIds.EveningReview);
        Assert.False(saved.Enabled);
        Assert.Equal("22:15", saved.At);
        Assert.Equal(AiRoutePolicy.PcOnly, saved.Route);
    }

    [Fact]
    public void Ask_Helms_route_is_one_choice_with_the_Proxy_tab()
    {
        var vm = Build();

        // The Proxy tab writes the same choice.
        _features.Update(s =>
        {
            var choice = s.For(AiFeatureIds.AskHelm).Copy();
            choice.Route = AiRoutePolicy.PcOnly;
            choice.PcWaitSeconds = 20;
            s.Features[AiFeatureIds.AskHelm] = choice;
        });
        var row = Row(vm, AiFeatureIds.AskHelm);
        Assert.Equal(AiRoutePolicy.PcOnly, row.Route.Policy);

        row.Model = Sonnet;
        Assert.Equal(20, _features.Current.For(AiFeatureIds.AskHelm).PcWaitSeconds);
        Assert.Equal(AiRoutePolicy.PcOnly, _features.Current.For(AiFeatureIds.AskHelm).Route);
    }

    [Fact]
    public void A_model_no_key_offers_now_stays_shown()
    {
        _features.Update(s => s.Features[AiFeatureIds.VocabQuick] = new AiFeatureChoice { Model = "claude-opus-5-5" });

        var row = Row(Build(Haiku), AiFeatureIds.VocabQuick);

        Assert.Equal("claude-opus-5-5", row.Model?.Model);
        Assert.Equal([ClaudeModels.Haiku, "claude-opus-5-5"], row.Models.Select(m => m.Model));
    }

    [Fact]
    public void The_summary_counts_todays_runs_on_agents_and_on_the_API()
    {
        var usage = new AiUsageBook(_usage);
        var vm = Build();
        Assert.Equal("No AI run today yet.", vm.SummaryText);

        usage.RecordAgent(AiFeatureIds.AskHelm, "Claude Code", ClaudeModels.Haiku, "PC-HUNG", 0.01m);
        usage.RecordAgent(AiFeatureIds.MorningBrief, "Claude Code", ClaudeModels.Haiku, "PC-HUNG", 0.01m);
        usage.Record(AiFeatureIds.AskHelm, AiProviders.Claude, "claude-sonnet-5-5", null, 1_000_000, 100_000, 0, 0);
        vm.Refresh();

        Assert.Equal("Today 3 runs · 2 on PC agents · 0đ · 1 on API · $3.00", vm.SummaryText);
    }

    [Fact]
    public void The_phone_opens_a_feature_and_goes_back_to_the_list()
    {
        var vm = Build();
        Assert.False(vm.Back());

        vm.OpenCommand.Execute(Row(vm, AiFeatureIds.HabitCoach));
        Assert.True(vm.IsDetail);

        Assert.True(vm.Back());
        Assert.True(vm.IsList);
    }
}
