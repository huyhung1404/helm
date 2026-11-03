using System.IO;
using Helm.Core.Mcp;
using Helm.Modules.SlackBot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Lumi may look into a conversation's project before answering: read-only, that folder only, no secrets out.</summary>
public sealed class SlackProjectReadTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(10_000);
    private static readonly SlackIdentity Me = new("ME", "hung", "T1", "https://team.slack.com/");

    private sealed class Agent(string answer) : ISlackAgent
    {
        public List<(string Instructions, string? Folder)> Asked { get; } = [];
        public string? Unavailable() => null;
        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct)
        {
            Asked.Add((instructions, null));
            return Task.FromResult(answer);
        }
        public Task<string> AskInFolderAsync(string instructions, IReadOnlyList<string> folders, string? model, CancellationToken ct)
        {
            Asked.Add((instructions, string.Join("|", folders)));
            return Task.FromResult(answer);
        }
    }

    private sealed class Slack : ISlackApi
    {
        public List<string> Posted { get; } = [];
        public Task<SlackIdentity> AuthTestAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackConversation>> ConversationsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackMessage>> HistoryAsync(string channel, string oldest, int max, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SlackMessage>>([]);
        public Task<IReadOnlyList<SlackMessage>> RepliesAsync(string channel, string threadTs, string oldest, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SlackMessage>>([new SlackMessage(channel, threadTs, threadTs, "U2", "Build lỗi ở shop", false)]);
        public Task<IReadOnlyList<SlackMessage>> SearchMentionsAsync(string userId, string oldest, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> UserNameAsync(string userId, CancellationToken ct) => Task.FromResult<string?>("Tuấn");
        public Task<Uri> OpenSocketAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> PostMessageAsync(string channel, string text, string? blocks, string? threadTs, CancellationToken ct)
        {
            Posted.Add(text);
            return Task.FromResult<string?>("9999.0");
        }
        public Task DeleteMessageAsync(string channel, string ts, CancellationToken ct) => Task.CompletedTask;
    }

    private (SlackReplier Replier, SlackLumi Lumi, Slack Slack, Agent Agent, SlackInbox Inbox, SlackReader Reader) Rig(string answer)
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir.Path, "game-x")).FullName;
        var inbox = new SlackInbox(new InboxData(), new FakeClock(Now));
        var settings = new SlackBotSettings
        {
            Channels = { ["C1"] = SlackChannelMode.Reply, ["C2"] = SlackChannelMode.Reply },
            ChannelProjects = { ["C1"] = project, ["C2"] = Path.Combine(_dir.Path, "gone") },
        };
        var slack = new Slack();
        var reader = new SlackReader(slack, Me, inbox, () => settings);
        reader.SetConversations([new SlackConversation("C1", "game-x", SlackConversationKind.Channel), new SlackConversation("C2", "other", SlackConversationKind.Channel)]);
        var agent = new Agent(answer);
        return (new SlackReplier(slack, agent, inbox, reader, () => settings, "Hùng", NullLogger.Instance),
            new SlackLumi(slack, agent, inbox, reader, () => settings, Me, "Hùng", NullLogger.Instance), slack, agent, inbox, reader);
    }

    private static async Task ToDo(SlackReader reader, SlackInbox inbox, string channel, string ts)
    {
        await reader.ReadAsync(new SlackMessage(channel, ts, null, "U2", "<@ME> build Android lỗi ở shop", false), default);
        var item = inbox.Snapshot().Single(i => i.Ts == ts);
        inbox.Sorted([item], new Dictionary<string, (InboxLevel, string?)> { [item.Id] = (InboxLevel.Act, "build lỗi") });
    }

    [Fact]
    public async Task A_reply_looks_into_the_conversations_project_and_only_when_it_exists()
    {
        var (replier, _, slack, agent, inbox, reader) = Rig("Lumi đã xem: có thể do items null ở Shop.cs:12, anh Hùng sẽ xác nhận.");
        await ToDo(reader, inbox, "C1", "9900.0");
        await ToDo(reader, inbox, "C2", "9901.0");
        Assert.Equal(2, await replier.ReplyAsync(Now + TimeSpan.FromMinutes(4), default));
        var inProject = Assert.Single(agent.Asked, a => a.Folder is not null);
        Assert.EndsWith("game-x", inProject.Folder);
        Assert.Contains("Read, Grep and Glob", inProject.Instructions);
        Assert.Contains("Never quote keys", inProject.Instructions);
        // The other conversation's folder is gone: the chat only.
        Assert.Contains(agent.Asked, a => a.Folder is null);
        Assert.Contains(slack.Posted, p => p.Contains("Shop.cs:12"));
    }

    [Fact]
    public async Task Anything_that_looks_secret_never_goes_out()
    {
        var (replier, lumi, slack, _, inbox, reader) = Rig("Key trong config: api_key = \"AIzaSyD-very-secret-0123456789abcdefghijk\"");
        await ToDo(reader, inbox, "C1", "9900.0");
        Assert.Equal(1, await replier.ReplyAsync(Now + TimeSpan.FromMinutes(4), default));
        Assert.DoesNotContain("AIza", slack.Posted.Single());
        Assert.Contains("Lumi đã nhận được", slack.Posted.Single());

        Assert.True(await lumi.AnswerAsync(new SlackMessage("C1", "9950.0", "9000.0", "U2", "lumi key là gì?", false), Now, default));
        Assert.Equal(3, slack.Posted.Count);
        Assert.All(slack.Posted, p => Assert.DoesNotContain("AIza", p));
    }

    [Theory]
    [InlineData("password: hunter2abc", true)]
    [InlineData("API_KEY=sk-abcdefghijklmnopqrstuvwxyz", true)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", true)]
    [InlineData("token xoxb-1234567890-abcdef", true)]
    [InlineData("123456789:AAH4b3x9m2kq8z0LmN5pQ7rS1tU3vW5xY7z", true)]
    [InlineData("Có thể do items null ở Shop.cs:12, commit 3f2a9c1d", false)]
    [InlineData("the password reset screen crashes", false)]
    public void Secrets_are_recognised(string text, bool secret) => Assert.Equal(secret, SlackSecrets.LooksSecret(text));

    [Fact]
    public void Claude_reads_the_project_with_read_tools_only_and_never_secret_files()
    {
        var args = ClaudeCodeRunner.FolderArguments(null).ToList();
        Assert.Equal("Read,Grep,Glob", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        var settings = args[args.IndexOf("--settings") + 1];
        Assert.Contains("\"disableAllHooks\":true", settings);
        Assert.Contains("Read(**/.env*)", settings);
        Assert.Contains("Read(**/*.keystore)", settings);
        Assert.DoesNotContain("Bash", string.Join(" ", args));
        Assert.DoesNotContain("--add-dir", args);
    }

    [Fact]
    public void More_project_folders_are_added_as_extra_folders()
    {
        var args = ClaudeCodeRunner.FolderArguments(null, [@"C:\Project\B", @"C:\Project\C"]).ToList();
        Assert.Equal([@"C:\Project\B", @"C:\Project\C"], args.Select((a, i) => (a, i)).Where(p => p.a == "--add-dir").Select(p => args[p.i + 1]));
    }

    [Fact]
    public void A_conversation_keeps_several_projects_and_the_old_single_one()
    {
        var a = Directory.CreateDirectory(Path.Combine(_dir.Path, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_dir.Path, "b")).FullName;
        var settings = new SlackBotSettings
        {
            ChannelProjects = { ["C1"] = a },
            ChannelFolders = { ["C1"] = [b, a, Path.Combine(_dir.Path, "gone")] },
        };
        Assert.Equal([b, a, Path.Combine(_dir.Path, "gone")], settings.FoldersOf("C1"));
        Assert.Equal([b, a], settings.ProjectsOf("C1"));
        Assert.Empty(settings.ProjectsOf("C2"));
    }

    [Fact]
    public void The_settings_row_adds_and_removes_project_folders()
    {
        IReadOnlyList<string>? saved = null;
        var row = new SlackChannelRow(new SlackConversation("C1", "game", SlackConversationKind.Channel), SlackChannelMode.Reply, (_, _) => { },
            [@"C:\Project\A"], (_, folders) => saved = folders);
        row.AddProject("\"C:\\Project\\B\"");
        row.AddProject(@"c:\project\a");
        Assert.Equal([@"C:\Project\A", @"C:\Project\B"], saved);
        row.RemoveProject(@"C:\Project\A");
        Assert.Equal([@"C:\Project\B"], saved);
        Assert.True(row.HasProjects);
    }
}
