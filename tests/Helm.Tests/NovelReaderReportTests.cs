using Helm.Core.Sync;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Reports;
using Helm.Shell.Services;

namespace Helm.Tests;

public sealed class NovelReaderReportTests
{
    private const string HanViet = "他=tha\n说=thuyết\n你=nhĩ\n好=hảo\n我=ngã\n走=tẩu\n了=liễu\n她=tha\n点=điểm\n头=đầu\n父=phụ\n亲=thân\n的=đích\n剑=kiếm\n";

    private const string VietPhrase = "他=hắn\n说=nói\n你好=xin chào\n我=ta\n走了=đi rồi\n她=nàng\n点头=gật đầu\n父亲=phụ thân\n的=của\n剑=kiếm\n";

    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));

    private static Converter NewConverter() =>
        new(new DictionarySet(PhraseDictionary.Parse(HanViet), PhraseDictionary.Parse(VietPhrase), new PhraseDictionary(), new PhraseDictionary(), new LuatNhanRules()));

    private static ReportContext Context(Converter converter) => new("book", "Novel", 4, "Chương 5", converter);

    [Fact]
    public void A_paragraph_is_cut_into_sentences_with_their_closing_quotes()
    {
        const string source = "他说: “你好. 我走了...” 她点头! 好";
        var sentences = ReportCapture.Sentences(source).Select(r => source.Substring(r.Start, r.Length)).ToList();

        Assert.Equal(["他说: “你好.", "我走了...”", "她点头!", "好"], sentences);
        var ranges = ReportCapture.Sentences(source);
        Assert.Equal(1, ReportCapture.SentenceAt(ranges, source.IndexOf('我')));
        // Between two sentences (the space): the one before.
        Assert.Equal(1, ReportCapture.SentenceAt(ranges, source.IndexOf('”') + 1));
    }

    [Fact]
    public void The_form_starts_on_the_clicked_sentence_and_widens_a_sentence_at_a_time()
    {
        var converter = NewConverter();
        var line = converter.ConvertLine("他说：“你好。我走了。”她点头。");
        var store = new TranslationReportStore(new MemorySynced<TranslationReport>(), _clock);
        var form = new ReportEditorViewModel(store, () => "Phone");

        form.Open(line, line.Source.IndexOf('走'), 2, Context(converter));

        Assert.True(form.IsOpen);
        Assert.Equal("我走了.”", form.Chinese);
        Assert.Equal("Ta đi rồi.”", form.Helm);
        Assert.Equal(form.Helm, form.Correct);
        Assert.Equal("走了", form.Word);
        Assert.True(form.CanAddBefore);
        Assert.True(form.CanAddAfter);
        Assert.False(form.CanDrop);

        form.AddAfterCommand.Execute(null);
        Assert.Equal("我走了.”她点头.", form.Chinese);
        // Untouched, the box follows Helm's text.
        Assert.Equal(form.Helm, form.Correct);
        form.AddBeforeCommand.Execute(null);
        Assert.StartsWith("他说: “你好.", form.Chinese);
        Assert.False(form.CanAddBefore);
        Assert.False(form.CanAddAfter);
        // Narrowing keeps the clicked sentence: first the one before it goes, then the one after.
        form.DropCommand.Execute(null);
        Assert.Equal("我走了.”她点头.", form.Chinese);
        form.DropCommand.Execute(null);
        Assert.Equal("我走了.”", form.Chinese);
    }

    [Fact]
    public void A_report_needs_a_right_translation_or_a_note_and_keeps_how_helm_cut_the_sentence()
    {
        var converter = NewConverter();
        var line = converter.ConvertLine("他父亲的剑。");
        var records = new MemorySynced<TranslationReport>();
        var store = new TranslationReportStore(records, _clock);
        var form = new ReportEditorViewModel(store, () => "Phone");
        form.Open(line, line.Source.IndexOf('的'), 1, Context(converter));
        Assert.Equal("Hắn phụ thân của kiếm.", form.Helm);

        form.SaveCommand.Execute(null);
        Assert.True(form.HasError);
        Assert.Empty(store.Reports());

        form.Correct = "Kiếm của phụ thân hắn.";
        form.Note = "的 phrase is reversed";
        form.SaveCommand.Execute(null);

        Assert.False(form.IsOpen);
        var (id, report, _) = Assert.Single(store.Reports());
        Assert.Equal("book", report.BookId);
        Assert.Equal("Novel", report.BookTitle);
        Assert.Equal(4, report.Chapter);
        Assert.Equal("Chương 5", report.ChapterTitle);
        Assert.Equal("他父亲的剑.", report.Chinese);
        Assert.Equal("Hắn phụ thân của kiếm.", report.Helm);
        Assert.Equal("Kiếm của phụ thân hắn.", report.Correct);
        Assert.Equal("的 phrase is reversed", report.Note);
        Assert.Equal("的", report.Word);
        Assert.Equal("Phone", report.Device);
        Assert.Equal(_clock.GetUtcNow(), report.CreatedAt);
        Assert.Equal(
            [new ReportPiece("他", "Hắn", "phrase"), new ReportPiece("父亲", "phụ thân", "phrase"), new ReportPiece("的", "của", "phrase"), new ReportPiece("剑", "kiếm", "phrase")],
            report.Pieces);

        // Editing changes the translation and the note only.
        form.Edit(id, report);
        Assert.Equal("Edit the report", form.Title);
        Assert.False(form.CanAddAfter);
        form.Note = "";
        form.SaveCommand.Execute(null);
        Assert.Equal("", store.Get(id)!.Note);
        Assert.Equal("Kiếm của phụ thân hắn.", store.Get(id)!.Correct);
    }

    [Fact]
    public async Task Asking_the_ai_saves_the_report_and_shows_its_suggestion_beside_the_readers_translation()
    {
        var converter = NewConverter();
        var line = converter.ConvertLine("他父亲的剑。");
        var store = new TranslationReportStore(new MemorySynced<TranslationReport>(), _clock);
        var agent = new FakeTranslator(store);
        var reports = new TranslationReportsViewModel(store, new InlineUi(), new MemoryClipboard(), new Phone(), () => Context(converter), agent);
        reports.Start(line, line.Source.IndexOf('的'), 1);
        var form = reports.Editor;
        Assert.True(form.CanAsk);

        await form.AskAiCommand.ExecuteAsync(null);

        // Saved first (Helm's own text left in the box is no translation), then asked about.
        var (id, saved, _) = Assert.Single(store.Reports());
        Assert.Equal(id, form.EditingId);
        Assert.Equal("", saved.Correct);
        Assert.Equal(["book"], agent.Books);
        Assert.Equal([id], agent.Ids);
        Assert.Equal("Kiếm của phụ thân hắn.", form.Suggestion);
        Assert.Equal("的 bị ngược", form.SuggestionNote);
        Assert.Equal(ErrorKinds.WordOrder, form.ErrorKind);
        Assert.Equal("Hắn phụ thân của kiếm.", form.Correct);
        Assert.Equal("Claude Code: Suggested 1 translation.", form.AskStatus);
        Assert.False(form.IsAsking);
        Assert.False(form.CanAddBefore);

        form.UseSuggestionCommand.Execute(null);
        form.SaveCommand.Execute(null);
        Assert.False(form.IsOpen);
        Assert.Equal("Kiếm của phụ thân hắn.", store.Get(id)!.Correct);

        // The list asks only about reports without a suggestion.
        reports.IsPanelOpen = true;
        var row = Assert.Single(reports.Rows);
        Assert.True(row.HasSuggestion);
        Assert.False(row.CanUseSuggestion);
        Assert.Equal("的 bị ngược · word-order", row.SuggestionNote);
        await reports.AskForRestCommand.ExecuteAsync(null);
        Assert.Equal("Every listed report has a suggestion already.", reports.AskStatus);
        Assert.Single(agent.Ids);
    }

    [Fact]
    public void Without_an_ai_nothing_offers_to_ask()
    {
        var store = new TranslationReportStore(new MemorySynced<TranslationReport>(), _clock);
        var reports = new TranslationReportsViewModel(store, new InlineUi(), new MemoryClipboard(), new Phone(), () => null);
        Assert.False(reports.CanAsk);
        Assert.False(reports.Editor.CanAsk);
        Assert.Equal("", reports.AskLabel);
    }

    [Fact]
    public void The_document_lists_each_report_and_whether_helm_gets_it_right_now()
    {
        var right = new TranslationReport
        {
            BookTitle = "Novel", ChapterTitle = "Chương 1", Chinese = "你好.", Helm = "Xin chào.", Correct = "Chào ngươi.", Note = "greeting",
            Word = "你好", Pieces = [new ReportPiece("你好", "xin chào", "phrase")], Paragraph = "你好. 我走了.",
            CreatedAt = _clock.GetUtcNow(), Device = "PC",
        };
        var wrong = right with { Chinese = "他父亲的剑.", Helm = "Hắn phụ thân của kiếm.", Correct = "Kiếm của phụ thân hắn.", Note = "", Pieces = [], Paragraph = "他父亲的剑." };
        var reports = new List<SyncedItem<TranslationReport>> { new("2", right, _clock.GetUtcNow()), new("1", wrong, _clock.GetUtcNow()) };

        var document = TranslationReportDocument.Build(reports, _clock.GetUtcNow(), r => r == right ? "chào  Ngươi." : "Hắn phụ thân của kiếm.");

        Assert.StartsWith("# Novel Reader: translation reports", document);
        Assert.Contains("2 reports · exported", document);
        Assert.Contains("· 1 right now", document);
        Assert.Contains("## 1. Novel · Chương 1", document);
        Assert.Contains("- **Chinese:** 你好.", document);
        Assert.Contains("- **Correct:** Chào ngươi.", document);
        Assert.Contains("- **Helm now:** chào  Ngươi. ✅", document);
        Assert.Contains("- **Pieces:** 你好=xin chào (phrase)", document);
        Assert.Contains("- **Paragraph:** 你好. 我走了.", document);
        Assert.Contains("- **Helm now:** Hắn phụ thân của kiếm.\n", document.ReplaceLineEndings("\n"));
        // A report's paragraph that is the sentence itself is not written twice.
        Assert.DoesNotContain("- **Paragraph:** 他父亲的剑.", document);
    }

    private sealed class Phone : IDeviceInfo
    {
        public string DeviceName => "Phone";
    }

    /// <summary>Writes its suggestions into the store, as Claude Code does through novel_suggest_translations.</summary>
    private sealed class FakeTranslator(TranslationReportStore store) : ITranslationAgent
    {
        public List<string> Books { get; } = [];
        public List<string> Ids { get; } = [];
        public string Name => "Claude Code";
        public string? Unavailable() => null;

        public Task<string> SuggestAsync(string bookId, string title, IReadOnlyList<string> reportIds, IProgress<string>? progress, CancellationToken ct)
        {
            Books.Add(bookId);
            Ids.AddRange(reportIds);
            progress?.Report("Claude Code is writing its suggestions… (3 steps)");
            foreach (var id in reportIds) store.SaveSuggestion(id, "Kiếm của phụ thân hắn.", "的 bị ngược", "Word-Order");
            return Task.FromResult("Suggested 1 translation.");
        }
    }
}
