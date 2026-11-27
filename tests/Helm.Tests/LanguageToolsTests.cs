using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Modules.NovelReader;
using Helm.Modules.Tracker;
using Helm.Modules.Vocab;

namespace Helm.Tests;

/// <summary>A held sentence's word going to Vocabulary, and a batch of tasks added to Tracker over MCP.</summary>
public sealed class LanguageToolsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly VocabStore _vocab;
    private readonly TrackerStore _tracker;

    public LanguageToolsTests()
    {
        _vocab = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>(), _time, new MemorySynced<VocabSource>());
        _tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
    }

    // ---- a held sentence ------------------------------------------------------------------------------------------

    /// <summary>The sentence a long press on 补救 lands in, as <see cref="HeldSentence.At"/> gives it.</summary>
    private static HeldSentence Held(string chinese = "他错过了最后的补救机会.") =>
        new(0, 0, 3, chinese, "tā cuò guò le", new SentenceWord("补救", "bǔ jiù", "bổ cứu"));

    [Fact]
    public void A_held_sentences_word_goes_to_Vocabulary_with_the_sentence_as_its_note_and_an_undo()
    {
        var held = new HeldSentenceViewModel([new VocabCaptureTarget(_vocab)]);

        held.Show(Held());

        Assert.True(held.IsShown);
        Assert.Equal("tā cuò guò le", held.Pinyin);
        // The held word, not ticked until the reader picks it.
        Assert.Equal("补救 bổ cứu", Assert.Single(held.Words).Label);
        Assert.False(held.AddWordsCommand.CanExecute(null));

        held.Words[0].Keep = true;
        Assert.Equal("Thêm 1 từ", held.AddWordsText);
        held.AddWordsCommand.Execute(null);

        Assert.Equal("他错过了最后的补救机会.", Assert.Single(_vocab.Words()).Value.Note);
        Assert.False(held.IsShown);
        Assert.Equal("Đã thêm 1 từ vào Vocabulary", held.Toast);
        held.UndoCommand.Execute(null);
        Assert.Empty(_vocab.Words());
        Assert.Equal("Đã hoàn tác", held.Toast);
        Assert.False(held.CanUndo);

        // Another sentence held: its own chip, no toast left over.
        held.DismissToastCommand.Execute(null);
        held.Show(Held("他走了."));
        Assert.Equal("他走了.", held.Sentence);
        Assert.Equal(["补救"], held.Words.Select(w => w.Word.Word));
        Assert.False(held.HasToast);
    }

    [Fact]
    public void A_word_already_there_is_said_so_and_without_Vocabulary_there_is_no_chip()
    {
        var held = new HeldSentenceViewModel([new VocabCaptureTarget(_vocab)]);
        _vocab.AddWord("补救");
        held.Show(Held());
        held.Words[0].Keep = true;
        held.AddWordsCommand.Execute(null);
        Assert.True(held.IsShown);
        Assert.Contains("already in your words", held.Problem, StringComparison.Ordinal);

        var alone = new HeldSentenceViewModel([]);
        alone.Show(Held());
        Assert.False(alone.HasWords);
        Assert.False(alone.AddWordsCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_speaker_says_the_held_sentence_and_shows_why_it_could_not()
    {
        var said = new List<string>();
        var held = new HeldSentenceViewModel([], s =>
        {
            said.Add(s);
            return Task.FromResult<string?>("No Chinese voice.");
        });

        held.Show(Held());
        await held.SayCommand.ExecuteAsync(null);

        Assert.Equal(["他错过了最后的补救机会."], said);
        Assert.Equal("No Chinese voice.", held.Problem);
        held.DismissCommand.Execute(null);
        Assert.False(held.IsShown);
    }

    // ---- Tracker: a batch of tasks over MCP --------------------------------------------------------------------

    [Fact]
    public async Task tracker_add_tasks_adds_a_batch_to_a_named_list_or_under_a_task_with_one_undo()
    {
        var tool = new TrackerMcpTools(_tracker).Tools.Single(t => t.Name == "tracker_add_tasks");
        _tracker.AddWorkspace("To-do");

        var result = await tool.Run(Json("""{"list": "Họp", "tasks": [{"title": "Gửi báo cáo", "due": "2026-11-06"}, {"title": "Đặt phòng"}]}"""), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);
        Assert.True(json.GetProperty("created_list").GetBoolean());
        Assert.Equal(2, json.GetProperty("ids").GetArrayLength());
        await tool.Undo!(json, CancellationToken.None);
        Assert.Single(_tracker.Workspaces());
        Assert.Empty(_tracker.AllItems());

        var list = _tracker.Workspaces()[0].Id;
        var parent = _tracker.AddItem(list, new TrackerItemDraft("Thi Aptis"));
        var steps = JsonSerializer.SerializeToElement(await tool.Run(Json($$"""{"parent": "{{parent}}", "tasks": [{"title": "Ôn"}, {"title": "Thi thử"}]}"""), CancellationToken.None));
        Assert.False(steps.GetProperty("created_list").GetBoolean());
        Assert.Equal(["Ôn", "Thi thử"], _tracker.Subtasks(parent).Select(s => s.Value.Title));

        await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Json("""{"tasks": [{"title": "x", "due": "thứ 6"}]}"""), CancellationToken.None));
        await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Json("""{"tasks": []}"""), CancellationToken.None));
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
