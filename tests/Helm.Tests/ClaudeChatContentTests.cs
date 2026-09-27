using System.Windows.Documents;
using Helm.Modules.ClaudeChat.Chat;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Tests;

public class ClaudeChatContentTests
{
    // ---- markdown ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Markdown_renders_the_common_blocks() => OnSta(() =>
    {
        const string md = """
            # Title
            Some **bold**, *italic*, ~~gone~~ and `code`.

            1. first
            2. second

            - [x] done
            - [ ] todo

            > quoted

            ```csharp
            var x = 1;
              indented();
            ```

            | Name | Value |
            |------|-------|
            | a    | 1     |

            ---
            """;

        var doc = MarkdownRenderer.Render(md);
        var blocks = doc.Blocks.ToList();

        var heading = Assert.IsType<Paragraph>(blocks[0]);
        Assert.Equal("Title", Text(heading));
        Assert.True(heading.FontSize > doc.FontSize);

        var paragraph = Assert.IsType<Paragraph>(blocks[1]);
        Assert.Equal("Some bold, italic, gone and code.", Text(paragraph));

        var ordered = Assert.IsType<List>(blocks[2]);
        Assert.Equal(System.Windows.TextMarkerStyle.Decimal, ordered.MarkerStyle);
        Assert.Equal(2, ordered.ListItems.Count);

        var tasks = Assert.IsType<List>(blocks[3]);
        Assert.Equal(System.Windows.TextMarkerStyle.None, tasks.MarkerStyle);
        var firstTask = Text(tasks.ListItems.First().Blocks.FirstBlock).TrimStart('\t'); // a list paragraph starts with the marker tab
        Assert.Equal("☑ done", firstTask);

        Assert.IsType<Section>(blocks[4]); // quote
        var code = Assert.IsType<Section>(blocks[5]);
        Assert.Equal("var x = 1;\n  indented();", Text(code.Blocks.FirstBlock).Replace("\r\n", "\n"));

        var table = Assert.IsType<Table>(blocks[6]);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(2, table.RowGroups[0].Rows.Count);
        Assert.Equal(System.Windows.FontWeights.SemiBold, table.RowGroups[0].Rows[0].FontWeight);

        Assert.IsType<Paragraph>(blocks[7]); // rule
    });

    [Fact]
    public void Single_newlines_stay_line_breaks() => OnSta(() =>
    {
        var p = Assert.IsType<Paragraph>(MarkdownRenderer.Render("one\ntwo").Blocks.FirstBlock);

        Assert.Contains(p.Inlines, i => i is LineBreak);
    });

    [Fact]
    public void Only_web_and_mail_links_are_clickable() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("[site](https://claude.com) [bad](javascript:alert(1)) [file](file:///C:/x) <https://example.com> <me@example.com>");

        var links = Descendants(doc).OfType<Hyperlink>().Select(h => h.NavigateUri!.ToString()).ToList();

        Assert.Equal(["https://claude.com/", "https://example.com/", "mailto:me@example.com"], links);
        Assert.Contains("bad", Text(doc.Blocks.FirstBlock));
    });

    [Theory]
    [InlineData("https://claude.com", true)]
    [InlineData("http://x.test/a?b=c", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    public void Safe_link_check(string url, bool safe) => Assert.Equal(safe, MarkdownRenderer.IsSafeLink(url, out _));

    [Fact]
    public void Unfinished_markdown_while_streaming_still_renders() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("Here:\n```py\nprint('hi')\n| a | b\n**bo");

        Assert.NotEmpty(doc.Blocks);
    });

    // ---- icon -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Claude_logo_is_a_frozen_vector_in_a_square_box()
    {
        var image = Assert.IsType<System.Windows.Media.DrawingImage>(ClaudeLogo.Image);

        Assert.True(image.IsFrozen);
        Assert.Equal(100, image.Width, 1);
        Assert.Equal(100, image.Height, 1);
    }

    [Fact]
    public void Module_icon_prefers_the_picture() => OnSta(() =>
    {
        var withPicture = new FakeModule(ClaudeLogo.Image);
        var withSymbol = new FakeModule(null);

        var picture = Assert.IsType<Wpf.Ui.Controls.ImageIcon>(Helm.Core.Ui.ModuleIcon.Create(withPicture, 32, 32));
        var symbol = Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(Helm.Core.Ui.ModuleIcon.Create(withSymbol, 32));

        Assert.Same(ClaudeLogo.Image, picture.Source);
        Assert.Equal(32, picture.Width);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Pin24, symbol.Symbol);
        Assert.Equal(32, symbol.FontSize);
    });

    private sealed class FakeModule(System.Windows.Media.ImageSource? image) : Helm.Core.Modules.HelmModuleBase
    {
        public override string Id => "fake";
        public override string DisplayName => "Fake";
        public override string Description => string.Empty;
        public override Helm.Core.Modules.ModuleGroup Group => Helm.Core.Modules.ModuleGroup.Advanced;
        public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Pin24;
        public override System.Windows.Media.ImageSource? IconImage => image;
        public override Type SettingsPageType => typeof(object);
        public override IReadOnlyList<Helm.Core.Hotkeys.HotkeyDefinition> Hotkeys => [];
        public override Task EnableAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task DisableAsync() => Task.CompletedTask;
    }

    // ---- saved sessions ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Project\Helm", "C--Project-Helm")]
    [InlineData(@"C:\Project\Helm\", "C--Project-Helm")]
    [InlineData(@"C:\Users\me\My Stuff (v2)", "C--Users-me-My-Stuff--v2-")]
    public void Folders_are_encoded_like_the_cli(string folder, string expected) => Assert.Equal(expected, ClaudeSessionStore.EncodeFolder(folder));

    [Fact]
    public void Sessions_are_listed_newest_first_with_titles()
    {
        using var root = new TempRoot();
        var dir = Directory.CreateDirectory(Path.Combine(root.Path, "c--work-app")).FullName; // lower-case drive, as VS Code writes it
        Write(dir, "old", DateTime.Now.AddHours(-2),
            """{"type":"user","isSidechain":false,"message":{"role":"user","content":"<command-name>/init</command-name>"}}""",
            """{"type":"user","isSidechain":false,"message":{"role":"user","content":"Fix the build please"}}""");
        Write(dir, "new", DateTime.Now,
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Add a README"}]}}""",
            """{"type":"ai-title","aiTitle":"Early title"}""",
            """{"type":"ai-title","aiTitle":"Write the project README"}""");
        Write(dir, "empty", DateTime.Now.AddHours(-1), """{"type":"queue-operation"}""", "not json");

        var sessions = ClaudeSessionStore.List(@"C:\work\app", projectsRoot: root.Path);

        Assert.Equal(["new", "old"], sessions.Select(s => s.SessionId));
        Assert.Equal("Write the project README", sessions[0].Title);
        Assert.Equal("Fix the build please", sessions[1].Title);
    }

    [Fact]
    public void Recent_chats_span_every_folder_with_their_real_folder()
    {
        using var root = new TempRoot();
        var helm = Directory.CreateDirectory(Path.Combine(root.Path, "C--Project-Helm")).FullName;
        var web = Directory.CreateDirectory(Path.Combine(root.Path, "c--Project-Web-App")).FullName;
        Write(helm, "h1", DateTime.Now.AddMinutes(-30),
            """{"type":"user","cwd":"C:\\Project\\Helm","message":{"role":"user","content":"Fix the tray icon"}}""");
        Write(web, "w1", DateTime.Now,
            """{"type":"queue-operation"}""",
            """{"type":"user","cwd":"C:\\Project\\Web-App","message":{"role":"user","content":"Deploy it"}}""",
            """{"type":"ai-title","aiTitle":"Deploy the web app"}""");

        var recent = ClaudeSessionStore.ListRecent(projectsRoot: root.Path);

        Assert.Equal(["w1", "h1"], recent.Select(r => r.SessionId));
        Assert.Equal(@"C:\Project\Web-App", recent[0].WorkingDirectory);
        Assert.Equal("Web-App", recent[0].FolderName);
        Assert.Equal("Deploy the web app", recent[0].Title);
        Assert.Equal("Helm", recent[1].FolderName);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(-5, "5m ago")]
    public void Relative_times_read_naturally(int minutes, string expected)
    {
        var now = new DateTime(2026, 9, 26, 15, 0, 0);
        Assert.Equal(expected, RelativeTimeConverter.Format(now.AddMinutes(minutes), now, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Older_times_become_days_then_dates()
    {
        var now = new DateTime(2026, 9, 26, 15, 0, 0);
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal("3h ago", RelativeTimeConverter.Format(now.AddHours(-3), now, culture));
        Assert.Equal("yesterday", RelativeTimeConverter.Format(now.AddDays(-1), now, culture));
        Assert.Equal("3d ago", RelativeTimeConverter.Format(now.AddDays(-3), now, culture));
        Assert.Equal("09/01/2026", RelativeTimeConverter.Format(new DateTime(2026, 9, 1), now, culture));
    }

    [Fact]
    public void Missing_folders_list_nothing()
    {
        using var root = new TempRoot();

        Assert.Empty(ClaudeSessionStore.List(@"C:\nowhere", projectsRoot: root.Path));
        Assert.Empty(ClaudeSessionStore.List(@"C:\nowhere", projectsRoot: Path.Combine(root.Path, "missing")));
    }

    [Fact]
    public void Transcript_keeps_the_main_conversation_only()
    {
        using var root = new TempRoot();
        var file = Write(root.Path, "s1", DateTime.Now,
            """{"type":"user","message":{"role":"user","content":"List files"}}""",
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":""},{"type":"text","text":"Sure."},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"a.txt\nb.txt"}]}}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"subagent prompt"}}""",
            """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"subagent reply"}]}}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"Caveat: hidden"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Two files."}]}}""",
            "{broken");

        var entries = ClaudeSessionStore.ReadTranscript(file);

        Assert.Collection(entries,
            e => Assert.Equal("List files", Assert.IsType<TranscriptUserText>(e).Text),
            e => Assert.Equal("Sure.", Assert.IsType<TranscriptAssistantText>(e).Text),
            e => Assert.Equal("Bash", Assert.IsType<TranscriptToolUse>(e).Name),
            e => Assert.Equal("a.txt\nb.txt", Assert.IsType<TranscriptToolResult>(e).Content),
            e => Assert.Equal("Two files.", Assert.IsType<TranscriptAssistantText>(e).Text));
    }

    [Fact]
    public void A_missing_transcript_reads_as_empty() => Assert.Empty(ClaudeSessionStore.ReadTranscript(@"C:\nope\missing.jsonl"));

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static string Write(string dir, string id, DateTime lastWrite, params string[] lines)
    {
        var path = Path.Combine(dir, id + ".jsonl");
        File.WriteAllLines(path, lines);
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    private static string Text(TextElement? element) =>
        element is null ? string.Empty : new TextRange(element.ContentStart, element.ContentEnd).Text;

    private static IEnumerable<object> Descendants(FlowDocument doc)
    {
        var stack = new Stack<object>(doc.Blocks.Reverse());
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren((System.Windows.DependencyObject)current).OfType<object>().Reverse())
            {
                if (child is System.Windows.DependencyObject) stack.Push(child);
            }
        }
    }

    /// <summary>FlowDocument elements need an STA thread.</summary>
    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "helm-sessions-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
