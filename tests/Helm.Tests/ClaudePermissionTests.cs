using System.Text.Json;
using Helm.Modules.ClaudeChat.Chat;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Tests;

public class ClaudePermissionTests
{
    // Shapes recorded from Claude Code 2.1.281 (paths shortened).
    private const string BashSuggestions = """
        [{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"mkdir alpha *"}],"behavior":"allow","destination":"localSettings"},
         {"type":"addDirectories","directories":["C:\\work"],"destination":"session"},
         {"type":"setMode","mode":"acceptEdits","destination":"session"}]
        """;

    private const string WriteSuggestions = """[{"type":"setMode","mode":"acceptEdits","destination":"session"}]""";

    [Fact]
    public void Every_option_is_scoped_to_the_session()
    {
        var options = PermissionOptions.From(Parse(BashSuggestions));

        Assert.Equal(3, options.Count);
        foreach (var option in options)
        {
            var update = Assert.Single(option.UpdatedPermissions.EnumerateArray());
            Assert.Equal("session", update.GetProperty("destination").GetString());
        }
    }

    [Fact]
    public void A_rule_suggested_for_local_settings_is_never_persisted()
    {
        var rule = PermissionOptions.From(Parse(BashSuggestions))[0];

        Assert.Equal("Allow mkdir alpha * for this chat", rule.Label);
        var update = rule.UpdatedPermissions[0];
        Assert.Equal("addRules", update.GetProperty("type").GetString());
        Assert.Equal("session", update.GetProperty("destination").GetString());
        Assert.Equal("mkdir alpha *", update.GetProperty("rules")[0].GetProperty("ruleContent").GetString());
        Assert.DoesNotContain("localSettings", rule.UpdatedPermissions.GetRawText());
    }

    [Fact]
    public void Labels_say_what_is_allowed()
    {
        var labels = PermissionOptions.From(Parse(BashSuggestions)).Select(o => o.Label).ToList();

        Assert.Equal(["Allow mkdir alpha * for this chat", @"Allow access to C:\work for this chat", "Allow all file edits for this chat"], labels);
    }

    [Fact]
    public void A_rule_without_content_covers_the_whole_tool()
    {
        var options = PermissionOptions.From(Parse("""[{"type":"addRules","rules":[{"toolName":"WebFetch"}],"behavior":"allow","destination":"userSettings"}]"""));

        Assert.Equal("Allow every WebFetch call for this chat", Assert.Single(options).Label);
    }

    [Theory]
    [InlineData("""[{"type":"setMode","mode":"bypassPermissions","destination":"session"}]""")]
    [InlineData("""[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm *"}],"behavior":"deny","destination":"session"}]""")]
    [InlineData("""[{"type":"somethingNew","destination":"session"}]""")]
    [InlineData("""[{"type":"addRules","rules":[],"behavior":"allow"}]""")]
    [InlineData("""[42, "text", null]""")]
    [InlineData("""{"not":"an array"}""")]
    public void Unsafe_or_unknown_suggestions_are_not_offered(string json) => Assert.Empty(PermissionOptions.From(Parse(json)));

    [Fact]
    public void Missing_suggestions_mean_no_extra_options() => Assert.Empty(PermissionOptions.From(null));

    [Fact]
    public async Task Card_answers_once_and_then_locks()
    {
        var answers = new List<(PermissionOption? Option, bool Allow)>();
        var card = new PermissionChatItem(Request(BashSuggestions), (_, option, allow) =>
        {
            answers.Add((option, allow));
            return Task.CompletedTask;
        });

        Assert.True(card.AllowOnceCommand.CanExecute(null));
        await card.AllowWithCommand.ExecuteAsync(card.Options[0]);

        Assert.Equal(PermissionState.AllowedWider, card.State);
        Assert.Equal("Allow mkdir alpha * for this chat", card.Outcome);
        Assert.False(card.DenyCommand.CanExecute(null));
        await card.DenyCommand.ExecuteAsync(null);
        var answer = Assert.Single(answers);
        Assert.True(answer.Allow);
        Assert.Same(card.Options[0], answer.Option);
    }

    [Fact]
    public async Task Card_deny_and_expiry()
    {
        var denied = new PermissionChatItem(Request(WriteSuggestions), (_, _, _) => Task.CompletedTask);
        await denied.DenyCommand.ExecuteAsync(null);
        denied.Expire();

        var unanswered = new PermissionChatItem(Request(WriteSuggestions), (_, _, _) => Task.CompletedTask);
        unanswered.Expire();

        Assert.Equal(PermissionState.Denied, denied.State);
        Assert.Equal("Denied", denied.Outcome);
        Assert.Equal(PermissionState.Expired, unanswered.State);
        Assert.False(unanswered.AllowOnceCommand.CanExecute(null));
    }

    [Fact]
    public void Card_shows_the_request()
    {
        var card = new PermissionChatItem(Request(WriteSuggestions), (_, _, _) => Task.CompletedTask);

        Assert.Equal("Claude wants to use Bash", card.Title);
        Assert.Equal("mkdir alpha beta", card.Summary);
        Assert.Contains("\"command\": \"mkdir alpha beta\"", card.Details);
        Assert.Equal("Allow all file edits for this chat", Assert.Single(card.Options).Label);
    }

    private static PermissionRequest Request(string suggestions) => new(
        "req-1", "Bash", "Bash", Parse("""{"command":"mkdir alpha beta","description":"Create two directories"}"""),
        "mkdir alpha beta", null, "toolu_1", Parse(suggestions));

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
