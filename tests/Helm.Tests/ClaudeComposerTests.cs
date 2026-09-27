using Helm.Modules.ClaudeChat.Chat;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Tests;

public class ClaudeComposerTests
{
    private static readonly SlashCommand[] s_commands =
    [
        new("compact", "Free up context by summarizing the conversation so far", "<optional custom summarization instructions>", true),
        new("cost", "Show usage", null, true),
        new("init", "Initialize a CLAUDE.md", null, true),
        new("git-flow", "Git hằng ngày cho project Unity/web", null, false),
    ];

    [Theory]
    [InlineData("/", 1, '/', 0, "")]
    [InlineData("/com", 4, '/', 0, "com")]
    [InlineData("@", 1, '@', 0, "")]
    [InlineData("look at @src/Ma", 15, '@', 8, "src/Ma")]
    [InlineData("a\n@doc", 6, '@', 2, "doc")]
    public void Triggers_are_found_at_the_caret(string text, int caret, char symbol, int start, string query)
    {
        var trigger = ComposerMenu.FindTrigger(text, caret);

        Assert.NotNull(trigger);
        Assert.Equal(symbol, trigger.Value.Symbol);
        Assert.Equal(start, trigger.Value.Start);
        Assert.Equal(query, trigger.Value.Query);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("hello", 5)]
    [InlineData("say /compact", 12)]       // "/" only opens the menu at the start
    [InlineData("/compact now", 12)]       // a space ends the command word
    [InlineData("mail me@example.com", 19)] // an address, not a mention
    [InlineData("@src ", 5)]                // the mention is finished
    public void Plain_text_opens_no_menu(string text, int caret) => Assert.Null(ComposerMenu.FindTrigger(text, caret));

    [Fact]
    public void Slash_menu_lists_actions_then_commands_then_skills()
    {
        var items = ComposerMenu.SlashItems("", s_commands);

        Assert.Equal(["Context", "Context", "Context", "Context", "Model", "Commands", "Commands", "Commands", "Skills"], items.Select(i => i.Group));
        Assert.Equal("/git-flow", items[^1].Insert);
        Assert.Equal(ComposerAction.SwitchModel, items[4].Action);
    }

    [Fact]
    public void Slash_menu_filters_and_prefers_prefix_matches()
    {
        var items = ComposerMenu.SlashItems("co", s_commands);

        Assert.Equal(["/compact", "/cost"], items.Where(i => i.Kind == SuggestionKind.Command).Select(i => i.Title));
        Assert.Contains(items, i => i.Title == "Export conversation"); // "conversation" contains "co"
        Assert.Contains("<optional custom summarization instructions>", items.First(i => i.Title == "/compact").Detail);
    }

    [Fact]
    public void File_menu_ranks_name_matches_and_marks_folders()
    {
        string[] paths = ["docs/", "docs/readme.md", "src/", "src/Main.cs", "src/Chat/MainWindow.xaml", "README.md"];

        var all = ComposerMenu.FileItems("", paths);
        var main = ComposerMenu.FileItems("main", paths);

        Assert.Equal(SuggestionKind.Folder, all.First(i => i.Title == "docs/").Kind);
        Assert.Equal(["Main.cs", "MainWindow.xaml"], main.Select(i => i.Title));
        Assert.Equal("src/", main[0].Detail);
        Assert.Equal("@src/Main.cs", main[0].Insert);
    }

    [Fact]
    public void Applying_a_suggestion_replaces_only_the_typed_word()
    {
        const string text = "compare @src/Ma with the docs";
        var trigger = ComposerMenu.FindTrigger(text, 14)!.Value; // caret inside the word: "@src/M|a"

        var (next, caret) = ComposerMenu.Apply(text, trigger, "@src/Main.cs");

        Assert.Equal("compare @src/Main.cs with the docs", next);
        Assert.Equal(21, caret); // after the space that already followed
    }

    [Fact]
    public void A_suggestion_at_the_end_gets_a_trailing_space()
    {
        var trigger = ComposerMenu.FindTrigger("/comp", 5)!.Value;

        Assert.Equal(("/compact ", 9), ComposerMenu.Apply("/comp", trigger, "/compact"));
        Assert.Equal((string.Empty, 0), ComposerMenu.Apply("/comp", trigger, string.Empty)); // an action clears the word
    }

    [Fact]
    public void Project_files_skip_build_output_and_use_forward_slashes()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "helm-files-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "Chat"));
            Directory.CreateDirectory(Path.Combine(root, "bin", "Debug"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "x"));
            File.WriteAllText(Path.Combine(root, "src", "Chat", "View.cs"), "");
            File.WriteAllText(Path.Combine(root, "bin", "Debug", "app.dll"), "");
            File.WriteAllText(Path.Combine(root, "README.md"), "");

            var files = ProjectFiles.List(root);

            Assert.Contains("src/Chat/View.cs", files);
            Assert.Contains("src/Chat/", files);
            Assert.Contains("README.md", files);
            Assert.DoesNotContain(files, f => f.StartsWith("bin", StringComparison.Ordinal) || f.StartsWith("node_modules", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Capabilities_are_read_from_the_initialize_reply()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"commands":[{"name":"compact","description":"Free up context","argumentHint":"<x>","builtin":true},
                         {"name":"__remote-workflow","description":"internal"},
                         {"name":"git-flow","description":"Git (user)","argumentHint":""}],
             "models":[{"value":"default","displayName":"Default (recommended)","description":"Opus 5.5"},{"value":"sonnet","displayName":"Sonnet"}]}
            """);

        var caps = ClaudeProtocol.ParseCapabilities(doc.RootElement);

        Assert.Equal(["compact", "git-flow"], caps.Commands.Select(c => c.Name));
        Assert.True(caps.Commands[0].IsBuiltIn);
        Assert.Null(caps.Commands[1].ArgumentHint);
        Assert.Equal(["default", "sonnet"], caps.Models.Select(m => m.Value));
        Assert.Equal("Default (recommended)", caps.Models[0].DisplayName);
    }
}
