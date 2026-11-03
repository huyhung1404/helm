using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Modules.Habits;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Helm.Modules.Vocab;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>Ask Helm sends a call only the tools its request needs: every schema costs tokens on every call.</summary>
public sealed class ToolScopeTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("trưa nay ăn phở 45 nghìn", "wallet")]
    [InlineData("Nam mượn anh 200k", "wallet")]
    [InlineData("tháng này anh tiêu bao nhiêu rồi", "wallet")]
    [InlineData("mai 9 giờ nhắc anh gọi khách", "tracker")]
    [InlineData("ghi chú lại ý tưởng app đọc truyện", "notes")]
    [InlineData("hôm nay anh chạy bộ rồi", "habit")]
    [InlineData("thêm từ vựng serendipity", "vocab")]
    [InlineData("6 rưỡi sáng mai gọi anh dậy", "phone")]
    public void A_request_asks_for_its_group(string said, string group) => Assert.Contains(group, ToolScope.GroupsFor(said));

    [Fact]
    public void A_request_that_names_no_group_gets_every_tool_and_a_later_one_adds_only_its_group()
    {
        var all = Assistant().OfferedTools([]);
        var scope = new HashSet<string>();

        Assert.Equal(all.Count, ToolScope.Pick(all, scope, "em ơi").Count);

        scope.Clear();
        var money = ToolScope.Pick(all, scope, "trưa nay ăn phở 45 nghìn");
        Assert.All(money, t => Assert.Equal("wallet", ToolScope.GroupOf(t.Name)));
        var more = ToolScope.Pick(all, scope, "nhắc anh trả tiền điện");
        Assert.Contains(more, t => t.Name == "tracker_add_task");
        Assert.Contains(more, t => t.Name == "wallet_add_transaction");
        Assert.DoesNotContain(more, t => t.Name.StartsWith("vocab_", StringComparison.Ordinal));
    }

    [Fact]
    public void Groups_follow_the_tool_names()
    {
        Assert.Equal("habit", ToolScope.GroupOf("habits_list"));
        Assert.Equal("habit", ToolScope.GroupOf("habit_check"));
        Assert.Equal("mission", ToolScope.GroupOf("missions_list"));
        Assert.Equal("notes", ToolScope.GroupOf("notes_create"));
        Assert.Equal("phone", ToolScope.GroupOf("phone_set_alarm"));
    }

    [Fact]
    public void A_money_request_carries_a_fraction_of_the_tools()
    {
        var all = Assistant().OfferedTools([]);
        var money = ToolScope.Pick(all, new HashSet<string>(), "trưa nay ăn phở 45 nghìn");
        var full = CliAgentProtocol.Preamble(HelmAssistant.Instructions, all).Length;
        var scoped = CliAgentProtocol.Preamble(HelmAssistant.Instructions, money).Length;
        Assert.True(scoped < full * 0.6, $"{scoped} of {full} characters.");
        if (Environment.GetEnvironmentVariable("HELM_CONTEXT_OUT") is { Length: > 0 } file)
            File.WriteAllLines(file,
            [
                $"instructions: {HelmAssistant.Instructions.Length} chars",
                $"all {all.Count} tools: {full} chars",
                $"wallet {money.Count} tools: {scoped} chars",
                .. all.Select(t => $"  {t.Name}: {t.Description.Length + t.Schema.ToJsonString().Length} chars"),
            ]);
    }

    private HelmAssistant Assistant()
    {
        var links = new LinkHub(new MemorySynced<HelmLink>(), () => [], _time);
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var vocab = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>(), _time);
        IMcpToolProvider[] providers =
        [
            new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time), wallet),
            new TrackerMcpTools(new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time)),
            new NotesMcpTools(new NotesStore(new MemorySynced<NoteItem>(), _time), links),
            new HabitsMcpTools(new HabitsStore(new MemorySynced<Habit>(), new MemorySynced<HabitCheck>(), new MemorySynced<HabitLapse>(), _time, TimeZoneInfo.Utc)),
            new MissionsMcpTools(new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>(), _time)),
            new VocabMcpTools(vocab, new VocabQuiz(vocab, new Random(1))),
        ];
        return new HelmAssistant(new AiKeyStore(new MemorySynced<AiKey>()), new MemorySettings<AiFeatureSettings>(), new AiUsageBook(new MemorySyncedLog<AiUsageEntry>()),
            providers, [], _ => true, new AiRouter([]));
    }
}
