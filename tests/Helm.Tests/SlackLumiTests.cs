using Helm.Modules.SlackBot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>"Lumi ơi" on Slack: who may call, what Lumi reads, where it answers.</summary>
public sealed class SlackLumiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(10_000);
    private static readonly SlackIdentity Me = new("ME", "hung", "T1", "https://team.slack.com/");

    /// <summary>C1's thread 9000.0 and C2's history; anything else would be a leak.</summary>
    private sealed class Slack : ISlackApi
    {
        public List<string> Read { get; } = [];
        public List<(string Channel, string Text, string? Blocks, string? Thread)> Posted { get; } = [];
        public List<(string Channel, string Ts)> Deleted { get; } = [];
        public Task<SlackIdentity> AuthTestAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackConversation>> ConversationsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackMessage>> HistoryAsync(string channel, string oldest, int max, CancellationToken ct)
        {
            Read.Add("history:" + channel);
            return Task.FromResult<IReadOnlyList<SlackMessage>>([new SlackMessage(channel, "9500.0", null, "U2", "Release 2.3 chốt thứ 6, link https://jira.example/X-1", false)]);
        }
        public Task<IReadOnlyList<SlackMessage>> RepliesAsync(string channel, string threadTs, string oldest, CancellationToken ct)
        {
            Read.Add($"replies:{channel}:{threadTs}");
            return Task.FromResult<IReadOnlyList<SlackMessage>>(
            [
                new SlackMessage(channel, threadTs, threadTs, "U2", "Build Android lỗi ở màn shop", false),
                new SlackMessage(channel, "9001.0", threadTs, "U3", "Đã fix ở commit abc, chờ QA", false),
            ]);
        }
        public Task<IReadOnlyList<SlackMessage>> SearchMentionsAsync(string userId, string oldest, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> UserNameAsync(string userId, CancellationToken ct) => Task.FromResult<string?>(userId switch { "U2" => "Tuấn", "U3" => "Linh", _ => null });
        public Task<Uri> OpenSocketAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> PostMessageAsync(string channel, string text, string? blocks, string? threadTs, CancellationToken ct)
        {
            Posted.Add((channel, text, blocks, threadTs));
            return Task.FromResult<string?>($"{9990 + Posted.Count}.0");
        }
        public Task DeleteMessageAsync(string channel, string ts, CancellationToken ct)
        {
            Deleted.Add((channel, ts));
            return Task.CompletedTask;
        }
    }

    private sealed class Agent(string answer) : ISlackAgent
    {
        public List<string> Asked { get; } = [];
        public string? Unavailable() => null;
        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct)
        {
            Asked.Add(instructions);
            return Task.FromResult(answer);
        }
    }

    private static (SlackLumi Lumi, Slack Slack, Agent Agent, SlackBotSettings Settings, SlackInbox Inbox) Rig(string answer = "Đã fix rồi, đang chờ QA.")
    {
        var inbox = new SlackInbox(new InboxData(), new FakeClock(Now));
        var settings = new SlackBotSettings { Channels = { ["C1"] = SlackChannelMode.Reply, ["C2"] = SlackChannelMode.Read } };
        var slack = new Slack();
        var reader = new SlackReader(slack, Me, inbox, () => settings);
        reader.SetConversations([
            new SlackConversation("C1", "dev", SlackConversationKind.Channel),
            new SlackConversation("C2", "random", SlackConversationKind.Channel),
            new SlackConversation("C3", "off", SlackConversationKind.Channel),
        ]);
        var agent = new Agent(answer);
        return (new SlackLumi(slack, agent, inbox, reader, () => settings, Me, "Hùng", NullLogger.Instance), slack, agent, settings, inbox);
    }

    [Theory]
    [InlineData("lumi tóm tắt thread này", true)]
    [InlineData("@lumi status?", true)]
    [InlineData("Lumi ơi, build sao rồi?", true)]
    [InlineData("hỏi LUMI xem", true)]
    [InlineData("<@U1> @lumi: check", true)]
    [InlineData("illuminate the scene", false)]
    [InlineData("lumière", false)]
    [InlineData("lumi2 build", false)]
    [InlineData("helm_lumi_bot", false)]
    public void A_call_is_lumi_oi_or_at_lumi(string text, bool call) => Assert.Equal(call, SlackLumi.IsCall(text));

    [Fact]
    public async Task Lumi_answers_in_the_thread_from_that_thread_only()
    {
        var (lumi, slack, agent, _, inbox) = Rig();
        var call = new SlackMessage("C1", "9002.0", "9000.0", "U2", "Lumi ơi, lỗi shop fix chưa?", false);
        Assert.True(await lumi.AnswerAsync(call, Now, default));
        Assert.Equal(["replies:C1:9000.0"], slack.Read);
        // "On it" first, in the thread, the asker tagged; taken away once the answer is in.
        Assert.Equal(2, slack.Posted.Count);
        Assert.Equal(("C1", "9000.0"), (slack.Posted[0].Channel, slack.Posted[0].Thread));
        Assert.Equal($"Lumi · trợ lý AI của Hùng: <@U2> {SlackLumi.Received}", slack.Posted[0].Text);
        Assert.Equal([("C1", "9991.0")], slack.Deleted);
        var post = slack.Posted[1];
        Assert.Equal(("C1", "9000.0"), (post.Channel, post.Thread));
        Assert.Contains(":robot_face: Lumi · trợ lý AI của Hùng", post.Blocks);
        // The one who asked is tagged.
        Assert.StartsWith("Lumi · trợ lý AI của Hùng: <@U2> Đã fix rồi", post.Text);
        Assert.Contains("Đã fix ở commit abc", agent.Asked[0]);
        Assert.Contains("Never promise", agent.Asked[0]);
        Assert.True(inbox.IsBotMessage("C1", "9991.0"));
        Assert.True(inbox.IsBotMessage("C1", "9992.0"));

        // Once per call.
        Assert.False(await lumi.AnswerAsync(call, Now, default));
    }

    [Fact]
    public async Task Others_need_write_and_the_user_may_call_anywhere()
    {
        var (lumi, slack, agent, _, _) = Rig();
        Assert.False(await lumi.AnswerAsync(new SlackMessage("C2", "9100.0", null, "U2", "Lumi ơi release khi nào?", false), Now, default));
        Assert.False(await lumi.AnswerAsync(new SlackMessage("C3", "9101.0", null, "U2", "Lumi ơi release khi nào?", false), Now, default));
        Assert.Empty(slack.Posted);

        Assert.True(await lumi.AnswerAsync(new SlackMessage("C2", "9102.0", null, "ME", "Lumi ơi release khi nào?", false), Now, default));
        Assert.Equal(["history:C2"], slack.Read);
        // Not in a thread: the answer starts one under the call. The user asked: nobody is tagged.
        Assert.All(slack.Posted, p => Assert.Equal("9102.0", p.Thread));
        Assert.All(slack.Posted, p => Assert.DoesNotContain("<@", p.Text));
        Assert.DoesNotContain("Never promise", agent.Asked[0]);
    }

    [Fact]
    public async Task Bots_lumis_own_posts_and_stale_calls_get_nothing()
    {
        var (lumi, slack, _, settings, inbox) = Rig();
        Assert.False(await lumi.AnswerAsync(new SlackMessage("C1", "9200.0", null, null, "Lumi ơi", true), Now, default));
        inbox.BotPosted("C1", "9201.0");
        Assert.False(await lumi.AnswerAsync(new SlackMessage("C1", "9201.0", null, "ME", "Lumi ơi", false), Now, default));
        Assert.False(await lumi.AnswerAsync(new SlackMessage("C1", "1000.0", null, "U2", "Lumi ơi", false), Now, default));
        settings.AnswerCalls = false;
        Assert.False(await lumi.AnswerAsync(new SlackMessage("C1", "9202.0", null, "U2", "Lumi ơi", false), Now, default));
        Assert.Empty(slack.Posted);
    }

    [Fact]
    public async Task Only_naming_lumi_gets_silence_when_claude_finds_nothing_asked()
    {
        var (lumi, slack, agent, _, _) = Rig(answer: "NO_REPLY");
        Assert.False(await lumi.AnswerAsync(new SlackMessage("C1", "9300.0", null, "U2", "Lumi trả lời rồi đó", false), Now, default));
        Assert.Single(agent.Asked);
        Assert.Contains("only mentions Lumi", agent.Asked[0]);
        // The "on it" line went, and went away again.
        Assert.Equal(SlackLumi.Received, Assert.Single(slack.Posted).Text.Split("> ")[1]);
        Assert.Equal([("C1", "9991.0")], slack.Deleted);
    }

    [Fact]
    public async Task The_user_calling_always_gets_a_word_back()
    {
        var (lumi, slack, agent, _, _) = Rig(answer: "NO_REPLY");
        Assert.True(await lumi.AnswerAsync(new SlackMessage("C2", "9400.0", null, "ME", "@lumi trả lời câu hỏi này", false), Now, default));
        Assert.Contains("always answer something", agent.Asked[0]);
        Assert.DoesNotContain("only mentions Lumi", agent.Asked[0]);
        Assert.Equal(["Lumi · trợ lý AI của Hùng: " + SlackLumi.Received, "Lumi · trợ lý AI của Hùng: " + SlackLumi.CouldNot("Hùng")],
            slack.Posted.Select(p => p.Text));
        Assert.Equal([("C2", "9991.0")], slack.Deleted);
    }

    [Fact]
    public void Answers_cannot_ping_or_bring_new_links()
    {
        var text = SlackLumi.Clean("<!here> Xem https://jira.example/X-1 và https://evil.example/login nhé <@U9>", ["link https://jira.example/X-1"]);
        Assert.Equal("Xem https://jira.example/X-1 và nhé", text);
        Assert.Null(SlackLumi.Clean("NO_REPLY", []));
    }
}
