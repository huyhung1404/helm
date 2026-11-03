using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Text;
using Helm.Embeddings;
using Helm.Modules.Notes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Helm.Tests;

/// <summary>
/// Notes found by meaning (O4): the indexer keeps each note's vectors in step with its text, notes_search and the
/// search box rank by meaning helped by words and recency, and without the model everything goes by words as before.
/// The model here is a stand-in that knows three topics by their words.
/// </summary>
public sealed class NoteMeaningTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    private readonly ITestOutputHelper _output;
    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<NoteVectors> _vectors = new();
    private readonly NotesStore _notes;
    private readonly TopicEmbedder _embedder = new();
    private readonly NoteIndexer _indexer;
    private readonly NoteMeaning _meaning;

    public NoteMeaningTests(ITestOutputHelper output)
    {
        _output = output;
        _notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        _indexer = new NoteIndexer(_notes, _vectors, _embedder, NullLogger<NoteIndexer>.Instance, TimeSpan.FromHours(1));
        _meaning = new NoteMeaning(_vectors, _embedder);
        _indexer.Start();
    }

    public void Dispose() => _indexer.Dispose();

    [Fact]
    public async Task Every_note_gets_vectors_and_an_edit_refreshes_only_that_note()
    {
        var server = _notes.Add("VPS", "Sập lúc 3 giờ sáng, Nginx trả 502.\n\nRestart pm2 thì hết.");
        var food = _notes.Add("Phở bò", "Xương bò hầm 6 tiếng");

        await _indexer.RefreshAsync();

        Assert.Equal(2, _embedder.Embedded);
        Assert.Equal((2, 2), (_indexer.Indexed, _indexer.Total));
        var vectors = _vectors.Get(server)!;
        Assert.Equal(TopicEmbedder.Id, vectors.Model);
        Assert.Equal(NoteVectors.HashOf(_notes.Get(server)!), vectors.Hash);
        Assert.Equal(2, vectors.Parts.Count);

        _notes.Save(food, "Phở bò", "Xương bò hầm 8 tiếng", _notes.Get(food)!.Rev);
        await _indexer.RefreshAsync();

        Assert.Equal(3, _embedder.Embedded);
        Assert.Equal(NoteVectors.HashOf(_notes.Get(food)!), _vectors.Get(food)!.Hash);
        Assert.Same(vectors, _vectors.Get(server));
    }

    [Fact]
    public async Task Vectors_of_another_model_are_made_again_and_a_note_deleted_for_good_takes_its_vectors()
    {
        var note = _notes.Add("VPS", "Nginx trả 502");
        var gone = _notes.Add("Cũ", "mua sữa");
        _vectors.Upsert(note, new NoteVectors { Model = "another-model", Hash = NoteVectors.HashOf(_notes.Get(note)!), Parts = ["AAAA"] });
        await _indexer.RefreshAsync();
        Assert.Equal(TopicEmbedder.Id, _vectors.Get(note)!.Model);

        _notes.MoveToTrash(gone);
        await _indexer.RefreshAsync();
        Assert.NotNull(_vectors.Get(gone));
        _notes.DeleteForever(gone);
        await _indexer.RefreshAsync();

        Assert.Null(_vectors.Get(gone));
        Assert.NotNull(_vectors.Get(note));
    }

    [Fact]
    public async Task Without_the_model_nothing_is_indexed_and_a_search_goes_by_words_as_before()
    {
        _embedder.Available = false;
        var note = _notes.Add("VPS", "Sập lúc 3 giờ sáng, Nginx trả 502.");
        await _indexer.RefreshAsync();

        Assert.Equal(0, _embedder.Embedded);
        Assert.Null(await _meaning.SimilaritiesAsync("lỗi server hôm trước"));
        Assert.Empty(await Search("""{"query":"Ghi chú về lỗi server hôm trước"}"""));
        Assert.Equal([note], await Search("""{"any":["lỗi server","nginx","502"]}"""));
        Assert.Equal([note], await Search("""{"query":"nginx"}"""));
    }

    [Fact]
    public async Task A_note_is_found_by_what_it_is_about_without_its_words()
    {
        var food = _notes.Add("Phở bò", "Xương bò hầm 6 tiếng");
        var server = _notes.Add("VPS", "Sập lúc 3 giờ sáng, Nginx trả 502.");
        _notes.Add("Đi chợ", "Mua sữa, trứng");
        await _indexer.RefreshAsync();

        Assert.Equal([server], await Search("""{"query":"Ghi chú về lỗi server hôm trước"}"""));
        Assert.Equal(food, (await Search("""{"query":"món ăn nấu"}"""))[0]);
        // No query: the newest, as before.
        Assert.Equal(3, (await Search("{}")).Count);
    }

    [Fact]
    public async Task Closer_in_meaning_comes_first_and_shared_words_and_recency_break_near_ties()
    {
        var close = _notes.Add("Sự cố", "Nginx 502, VPS sập, crash");
        var some = _notes.Add("Deploy", "deploy xong thì phở bò ngon");
        _time.Advance(TimeSpan.FromDays(60));
        var twinOld = _notes.Add("A", "server sập");
        _time.Advance(TimeSpan.FromDays(60));
        var twinNew = _notes.Add("B", "server sập");
        await _indexer.RefreshAsync();

        var meaning = (await _meaning.SimilaritiesAsync("server sập"))!;
        var ranked = NoteRecall.Rank(_notes.Notes(), TextSearch.Terms("server sập"), [], meaning, _time.Now).Select(n => n.Id).ToList();

        // The twins share the words too: they come before the note that is only about the same thing, the newer first;
        // the note half about servers comes last.
        Assert.Equal([twinNew, twinOld, close, some], ranked);
        Assert.True(meaning[close] > meaning[some]);
    }

    [Fact]
    public async Task The_search_box_lists_the_notes_with_the_words_then_the_ones_about_the_same_thing()
    {
        using var dir = new TempDir();
        using var settings = new SettingsStoreFactory(new HelmPaths(dir.Path));
        var server = _notes.Add("VPS", "Sập lúc 3 giờ sáng, Nginx trả 502.");
        var words = _notes.Add("Server mới", "Đặt mua server ở Hetzner");
        _notes.Add("Phở bò", "Xương bò hầm 6 tiếng");
        await _indexer.RefreshAsync();
        var ui = new SerialUi();
        using var page = new NotesViewModel(_notes, settings, ui, new AcceptDialogs(), new MemoryClipboard(), NullLogger<NotesViewModel>.Instance, meaning: _meaning);

        ui.Run(() => page.Search = "server");
        Assert.Equal([words], ui.Run(() => page.Notes.Select(n => n.Id).ToList()));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ui.Run(() => page.Notes.Count) < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal([words, server], ui.Run(() => page.Notes.Select(n => n.Id).ToList()));
    }

    [Fact]
    public async Task The_real_model_finds_a_note_about_a_server_failure_by_what_it_is_about()
    {
        if (SentenceEmbeddingTests.RealEmbedder() is not { } embedder) return;
        using var _ = embedder;
        var notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        var vectors = new MemorySynced<NoteVectors>();
        string[] texts =
        [
            "VPS sập lúc 3 giờ sáng\nNginx trả 502, pm2 restart thì hết. Nguyên nhân: hết RAM do Mongo.",
            "Công thức phở bò\nXương bò hầm 6 tiếng, quế hồi gừng nướng.",
            "Meeting notes with the design team about the new onboarding flow",
            "Danh sách mua sắm: sữa, trứng, bánh mì",
            "Server down again: the API returned 500 errors after the deploy, rolled back",
            "Ý tưởng app học từ vựng tiếng Anh bằng gốc từ",
            "Lịch tập gym tuần này: thứ 2 ngực, thứ 4 lưng, thứ 6 chân",
            "Sinh nhật mẹ 15/03, mua quà: khăn lụa",
            "Đọc xong sách Atomic Habits: thói quen nhỏ mỗi ngày",
            "Chi tiêu tháng 10: tiền nhà 5 triệu, điện 600k, ăn uống 4 triệu",
            "Bug: app Android crash khi mở camera trên Android 14",
        ];
        var ids = texts.Select(t => notes.Add("", t)).ToList();
        using var indexer = new NoteIndexer(notes, vectors, embedder, NullLogger<NoteIndexer>.Instance, TimeSpan.FromHours(1));
        indexer.Start();
        await embedder.EmbedAsync("xin chào");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await indexer.RefreshAsync();
        var perNote = watch.Elapsed.TotalMilliseconds / texts.Length;
        var meaning = new NoteMeaning(vectors, embedder);
        var search = new NotesMcpTools(notes, meaning: meaning).Tools.Single(t => t.Name == "notes_search");
        watch.Restart();
        var found = JsonSerializer.SerializeToElement(await search.Run(JsonDocument.Parse("""{"query":"Ghi chú về lỗi server hôm trước"}""").RootElement, default))
            .EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToList();
        var perQuery = watch.Elapsed.TotalMilliseconds;

        var close = (await meaning.SimilaritiesAsync("Ghi chú về lỗi server hôm trước"))!;
        _output.WriteLine(string.Join('\n', ids.Select((id, i) => $"{close[id]:0.000} {(found.Contains(id) ? "found" : "-")} {texts[i][..30]}")));
        _output.WriteLine($"{perNote:0} ms a note, {perQuery:0} ms a search");
        Assert.Equal((texts.Length, texts.Length), (indexer.Indexed, indexer.Total));
        // The two notes about a server going down, in Vietnamese and in English, and none about food or shopping.
        Assert.Equal([ids[0], ids[4]], found.Take(2).Order());
        Assert.DoesNotContain(ids[1], found);
        Assert.DoesNotContain(ids[3], found);
        Assert.True(perNote < 200 && perQuery < 200, $"{perNote:0} ms a note, {perQuery:0} ms a search");
    }

    [Fact]
    public void Notes_settings_offer_the_model_to_download_with_its_size()
    {
        using var dir = new TempDir();
        var installer = new SentenceModelInstaller(new SentenceModelFiles(dir.Path), NullLogger<SentenceModelInstaller>.Instance);
        var row = new NoteSearchModelViewModel(installer, _indexer, new InlineUi(), new AcceptDialogs());

        Assert.False(row.IsInstalled);
        Assert.True(row.CanDownload);
        Assert.Equal("129 MB", row.Size);
        Assert.StartsWith("Not on this device: notes are found by their words.", row.Status);
    }

    private async Task<List<string?>> Search(string args)
    {
        var search = new NotesMcpTools(_notes, meaning: _meaning).Tools.Single(t => t.Name == "notes_search");
        var found = JsonSerializer.SerializeToElement(await search.Run(JsonDocument.Parse(args).RootElement, default));
        return [.. found.EnumerateArray().Select(n => n.GetProperty("id").GetString())];
    }

    /// <summary>A stand-in model: a text points at the topics its words belong to (servers, food, shopping).</summary>
    private sealed class TopicEmbedder : ITextEmbedder
    {
        public const string Id = "topics";

        private static readonly string[][] Topics =
        [
            ["server", "vps", "nginx", "502", "500", "sap", "crash", "loi", "deploy"],
            ["pho", "bo", "an", "nau", "mon", "xuong"],
            ["mua", "cho", "sua", "trung"],
        ];

        public bool Available { get; set; } = true;

        public int Embedded { get; private set; }

        public string ModelId => Id;

        public bool IsAvailable => Available;

        public event EventHandler? AvailabilityChanged { add { } remove { } }

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Available ? Vector(text) : null);

        public Task<IReadOnlyList<float[]>> EmbedPartsAsync(string text, CancellationToken ct = default)
        {
            if (!Available) return Task.FromResult<IReadOnlyList<float[]>>([]);
            Embedded++;
            return Task.FromResult<IReadOnlyList<float[]>>([.. text.Split("\n\n").Select(Vector)]);
        }

        private static float[] Vector(string text)
        {
            var words = TextSearch.Terms(text.Replace(',', ' ').Replace('.', ' '));
            var vector = Topics.Select(t => (float)words.Count(t.Contains)).Append(0.3f).ToArray();
            EmbeddingVector.Normalize(vector);
            return vector;
        }
    }
}
