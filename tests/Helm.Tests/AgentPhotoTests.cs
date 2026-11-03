using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// Ask Helm's photo turns on a PC agent first: the photo travels as a blob of its own (never in the job's record), the
/// PC's agent gets it (claimed only once fetched), Claude Code and Codex are handed it in their own shapes, an agent
/// that reads none declines, and nothing of it is kept.
/// </summary>
public sealed class AgentPhotoTests : IDisposable
{
    private static readonly AssistantImage Photo = new([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly List<IDisposable> _owned = [];
    private readonly MemorySynced<AiJobRecord> _jobs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static AiJob Job(params AssistantImage[] images) =>
        new(AiFeatureIds.AskHelm, "conv-photo", "Read the photo.", ["wallet_add_transaction"], [], "claude-haiku-5-5", "low") { Images = images };

    // ---- through sync -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_photo_reaches_the_PC_as_a_blob_and_is_gone_from_both_devices_and_the_server_after_the_turn()
    {
        var (phoneBlobs, phoneFolder) = Store("phone");
        var (pcBlobs, pcFolder) = Store("pc");
        var pc = new PhotoReader(new AiJobResult("Đã ghi hoá đơn.", [], [], 0.002m));
        // The engine uploads a blob before it pushes the record that names it: here the phone uploads at once.
        var phonePhotos = new UploadingPhotos(new BlobJobPhotos(phoneBlobs), phoneBlobs);
        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", () => ["Claude Code"], photos: new BlobJobPhotos(pcBlobs));
        worker.Start();
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id", photos: phonePhotos);
        Assert.True(phone.ReadsPhotos);
        var photo = new AssistantImage(RandomNumberGenerator.GetBytes(600 * 1024));
        JsonNode? seen = null;
        _jobs.Changed += (_, e) =>
        {
            if (seen is null && _jobs.Get(e.Ids[0]) is { Photos: { Count: 1 } } record) seen = JsonSerializer.SerializeToNode(record, SyncJson.Options);
        };

        var result = await phone.RunAsync(Job(photo), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Đã ghi hoá đơn.", result.Text);
        Assert.Equal(photo.Data, Assert.Single(Assert.Single(pc.Jobs).Images).Data);
        // The record held the blob's reference, never the picture: a few hundred bytes, far below the 1 MiB a record may be.
        var json = seen!.ToJsonString();
        Assert.True(json.Length < 2048, $"{json.Length} bytes");
        Assert.DoesNotContain(Convert.ToBase64String(photo.Data)[..64], json);
        Assert.Null(seen["job"]!["images"]);
        Assert.Empty(_jobs.All());
        // Gone: in the server's trash (purged from there by its clean-up), and no chunk left on either device.
        var blob = Assert.Single(_server.Blobs.Values);
        await Until(() => blob.DeletedAt is not null);
        Assert.False(Directory.Exists(Path.Combine(phoneFolder, blob.Id)));
        Assert.False(Directory.Exists(Path.Combine(pcFolder, blob.Id)));
    }

    [Fact]
    public async Task A_PC_that_cannot_fetch_the_photo_declines_before_claiming_and_the_phone_goes_on_at_once()
    {
        var pc = new PhotoReader(new AiJobResult("never", [], [], null));
        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", () => ["Codex"], photos: new FailingPhotos());
        worker.Start();
        var claims = new List<string>();
        _jobs.Changed += (_, e) =>
        {
            if (_jobs.Get(e.Ids[0]) is { } record) lock (claims) claims.Add(record.State);
        };
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id", photos: new MemoryPhotos());
        var asked = DateTimeOffset.UtcNow;

        // An AiRouteException, not a taken one: the router tries the next way (the API).
        var declined = await Assert.ThrowsAsync<AiRouteException>(() => phone.RunAsync(Job(Photo), TimeSpan.FromSeconds(20), CancellationToken.None));

        Assert.IsNotType<AiRouteTakenException>(declined);
        Assert.Contains("could not fetch the photo", declined.Message);
        Assert.True(DateTimeOffset.UtcNow - asked < TimeSpan.FromSeconds(5));
        lock (claims) Assert.DoesNotContain(AgentJobs.Claimed, claims);
        Assert.Empty(pc.Jobs);
        Assert.Empty(_jobs.All());
    }

    [Fact]
    public void The_record_of_a_photo_job_syncs_after_its_blob()
    {
        var options = new SyncedCollectionOptions<AiJobRecord> { Name = AgentJobs.JobsCollection, BlobReferences = AgentJobs.PhotosOf };
        var blob = new BlobRef("01JPHOTO0000000000000000AA", 7, BlobStore.ChunkSize, 1, new byte[32]);
        var record = new AiJobRecord { To = "pc", From = "phone", Job = Job(Photo), Photos = [new AiJobPhoto(blob, "image/jpeg")] };

        var named = options.ToDescriptor().BlobReferences!(JsonSerializer.Serialize(record, SyncJson.Options));

        Assert.Equal([blob.Id], named);
        Assert.Empty(AgentJobs.PhotosOf(record with { Photos = null }));
    }

    [Fact]
    public async Task No_photo_goes_to_a_PC_whose_agents_read_none_or_that_is_older()
    {
        var photos = new MemoryPhotos();
        // A Helm from before photos never says it reads them.
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Grok"], SeenAt = DateTimeOffset.UtcNow });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id", photos: photos);

        Assert.Null(phone.Unavailable());
        Assert.False(phone.ReadsPhotos);
        await Assert.ThrowsAsync<AiRouteException>(() => phone.RunAsync(Job(Photo), TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Empty(_jobs.All());
        Assert.Empty(photos.Stored);
        // Nor from a phone where photos cannot travel.
        Assert.False(new SyncAgentRoute(_jobs, _workers, () => "phone-id").ReadsPhotos);
    }

    [Fact]
    public void The_PC_says_it_reads_photos_only_when_an_agent_that_reads_them_is_ready()
    {
        var reader = new PhotoReader(new AiJobResult("", [], [], null));
        using (var worker = new AgentJobWorker(_jobs, _workers, reader, () => "pc-id", "PC-HUNG", () => ["Codex"], photos: new MemoryPhotos()))
        {
            worker.Beat();
            Assert.True(_workers.Get("pc-id")!.ReadsPhotos);
        }
        using (var worker = new AgentJobWorker(_jobs, _workers, new PhotoReader(new AiJobResult("", [], [], null), reads: false), () => "pc-id", "PC-HUNG", () => ["Grok"], photos: new MemoryPhotos()))
        {
            worker.Beat();
            Assert.False(_workers.Get("pc-id")!.ReadsPhotos);
        }
        using (var worker = new AgentJobWorker(_jobs, _workers, reader, () => "pc-id", "PC-HUNG", () => ["Codex"]))
        {
            worker.Beat();
            Assert.False(_workers.Get("pc-id")!.ReadsPhotos);
        }
    }

    [Fact]
    public async Task A_photo_job_withdrawn_before_a_PC_took_it_drops_the_photo_and_its_record_names_it_no_more()
    {
        var photos = new MemoryPhotos();
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Codex"], ReadsPhotos = true, SeenAt = DateTimeOffset.UtcNow });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id", photos: photos);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => phone.RunAsync(Job(Photo), TimeSpan.FromSeconds(30), cancel.Token));

        var left = Assert.Single(_jobs.All()).Value;
        Assert.Equal(AgentJobs.Cancelled, left.State);
        Assert.Null(left.Photos);
        Assert.Single(photos.Dropped);
        Assert.Empty(photos.Stored);
    }

    // ---- on the PC ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_the_PC_the_agent_gets_the_photo_with_the_words_and_its_chat_ends_with_the_turn()
    {
        var agent = new Agent(reads: true, "Đã ghi.");
        using var local = Local("Claude Code", agent);

        var result = await local.RunAsync(Job(Photo), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Đã ghi.", result.Text);
        var chat = Assert.Single(agent.Chats);
        Assert.Equal(("Read the photo.", Photo), Assert.Single(chat.Photos));
        Assert.True(chat.Disposed);
    }

    [Fact]
    public async Task An_agent_that_reads_no_photos_declines_and_the_chain_hands_the_photo_to_one_that_does()
    {
        var grok = new Agent(reads: false, "never");
        var claude = new Agent(reads: true, "Claude đọc ảnh.");
        using var grokRoute = Local("Grok", grok);
        using var claudeRoute = Local("Claude Code", claude);

        await Assert.ThrowsAsync<AiRouteException>(() => grokRoute.RunAsync(Job(Photo), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(grokRoute.ReadsPhotos);
        Assert.Empty(grok.Chats);

        var chain = new AgentChain([grokRoute, claudeRoute]);
        Assert.True(chain.ReadsPhotos);
        var result = await chain.RunAsync(Job(Photo), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Claude đọc ảnh.", result.Text);
        Assert.Empty(grok.Chats);
        // Words still go to the first agent.
        Assert.Equal("never", (await chain.RunAsync(Job() with { ConversationId = "words" }, TimeSpan.FromSeconds(5), CancellationToken.None)).Text);
        Assert.False(new AgentChain([grokRoute]).ReadsPhotos);
    }

    [Fact]
    public async Task An_agent_that_turns_the_photo_down_gives_way_without_taking_the_turn()
    {
        using var local = Local("Claude Code", new Agent(reads: true, "", refuse: true));

        var ex = await Assert.ThrowsAsync<AiRouteException>(() => local.RunAsync(Job(Photo), TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.IsNotType<AiRouteTakenException>(ex);
    }

    // ---- the agents' own shapes ---------------------------------------------------------------------------------

    [Fact]
    public void Claude_Code_gets_the_photo_as_a_base64_image_block_of_its_stream_json_message()
    {
        var content = CliImages.ClaudeCodeContent("Read the photo.", [Photo]).AsArray();

        Assert.Equal("text", (string)content[0]!["type"]!);
        Assert.Equal("Read the photo.", (string)content[0]!["text"]!);
        Assert.Equal("image", (string)content[1]!["type"]!);
        Assert.Equal("base64", (string)content[1]!["source"]!["type"]!);
        Assert.Equal("image/jpeg", (string)content[1]!["source"]!["media_type"]!);
        Assert.Equal(Convert.ToBase64String(Photo.Data), (string)content[1]!["source"]!["data"]!);
        // Words alone stay a string, as before.
        Assert.Equal("hi", CliImages.ClaudeCodeContent("hi", []).GetValue<string>());
    }

    [Fact]
    public void The_conversation_keeps_the_photo_until_a_turn_went_through_and_a_recap_only_says_one_was_sent()
    {
        var chat = new CliConversation("Be Helm.", []);
        chat.AddUser("Read the photo.", Photo);
        _ = chat.Message(newSession: true); // the turn fails: no Answered

        Assert.Same(Photo, Assert.Single(chat.Images));
        Assert.True(chat.HasPending);
        chat.Answered("Đã ghi.");
        Assert.Empty(chat.Images);
        Assert.False(chat.HasPending);

        chat.AddUser("cảm ơn");
        var recap = chat.Message(newSession: true);
        Assert.Contains("(the user sent a photo) Read the photo.", recap);
        Assert.DoesNotContain(Convert.ToBase64String(Photo.Data), recap);
    }

    [Fact]
    public void Codex_gets_the_photo_as_a_data_url_input_of_its_turn()
    {
        var input = CliImages.CodexInput("Read the photo.", [Photo, new AssistantImage([1, 2], "image/png")]);

        Assert.Equal(3, input.Count);
        Assert.Equal("text", (string)input[0]!["type"]!);
        Assert.Equal("image", (string)input[1]!["type"]!);
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(Photo.Data)}", (string)input[1]!["url"]!);
        Assert.Equal("data:image/png;base64,AQI=", (string)input[2]!["url"]!);
    }

    [Fact]
    public void Codex_exec_gets_photo_files_before_a_double_dash_and_the_prompt_from_stdin()
    {
        string[] fresh = ["exec", "--json", "--cd", "work", "-"];
        Assert.Equal(["exec", "--json", "--cd", "work", "--image", "a.jpg", "--image", "b.png", "--", "-"],
            CliImages.WithCodexImages(fresh, ["a.jpg", "b.png"]));
        Assert.Equal(["exec", "--json", "resume", "t-1", "--image", "a.jpg", "--", "-"],
            CliImages.WithCodexImages(["exec", "--json", "resume", "t-1", "-"], ["a.jpg"]));
        Assert.Equal(fresh, CliImages.WithCodexImages(fresh, []));
    }

    [Fact]
    public void Photo_files_for_a_run_are_written_into_the_folder_and_deleted_after()
    {
        Directory.CreateDirectory(_dir);
        var files = CliImages.Save(_dir, [Photo, new AssistantImage([9], "image/png")]);

        Assert.Equal(2, files.Count);
        Assert.Equal(Photo.Data, File.ReadAllBytes(files[0]));
        Assert.EndsWith(".jpg", files[0]);
        Assert.EndsWith(".png", files[1]);
        CliImages.Delete(files);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    // ---- doubles ------------------------------------------------------------------------------------------------

    private (BlobStore Store, string Folder) Store(string name)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local);
        _owned.Add(db);
        return (new BlobStore(db, _server.Connect(), paths.SyncBlobsDirectory), paths.SyncBlobsDirectory);
    }

    private static LocalAgentRoute Local(string name, IAssistantChatFactory agent) =>
        new("PC-HUNG", name, agent, [new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), Wallet()), Wallet())], _ => true, () => null);

    private static WalletStore Wallet() => new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    private sealed class UploadingPhotos(IAgentJobPhotos inner, BlobStore store) : IAgentJobPhotos
    {
        public async Task<AiJobPhoto> PutAsync(AssistantImage image, CancellationToken ct)
        {
            var photo = await inner.PutAsync(image, ct);
            await store.UploadPendingAsync(ct);
            return photo;
        }

        public Task<AssistantImage> ReadAsync(AiJobPhoto photo, CancellationToken ct) => inner.ReadAsync(photo, ct);

        public void Drop(AiJobPhoto photo) => inner.Drop(photo);
    }

    private sealed class MemoryPhotos : IAgentJobPhotos
    {
        public Dictionary<string, AssistantImage> Stored { get; } = [];
        public List<string> Dropped { get; } = [];

        public Task<AiJobPhoto> PutAsync(AssistantImage image, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N");
            Stored[id] = image;
            return Task.FromResult(new AiJobPhoto(new BlobRef(id, image.Data.Length, BlobStore.ChunkSize, 1, new byte[32]), image.MediaType));
        }

        public Task<AssistantImage> ReadAsync(AiJobPhoto photo, CancellationToken ct) => Task.FromResult(Stored[photo.Blob.Id]);

        public void Drop(AiJobPhoto photo)
        {
            Stored.Remove(photo.Blob.Id);
            Dropped.Add(photo.Blob.Id);
        }
    }

    /// <summary>A PC's side of a blob that never arrives.</summary>
    private sealed class FailingPhotos : IAgentJobPhotos
    {
        public Task<AiJobPhoto> PutAsync(AssistantImage image, CancellationToken ct) => throw new NotSupportedException();

        public Task<AssistantImage> ReadAsync(AiJobPhoto photo, CancellationToken ct) => throw new IOException("the blob is not on the server");

        public void Drop(AiJobPhoto photo) { }
    }

    /// <summary>A PC whose agent reads photos (or not), keeping the jobs it ran.</summary>
    private sealed class PhotoReader(AiJobResult result, bool reads = true) : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public string Device => "PC-HUNG";
        public bool ReadsPhotos => reads;
        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            return Task.FromResult(result);
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;

        public void End(string conversationId) { }
    }

    /// <summary>An agent CLI's chats that read photos (or not), answering one text.</summary>
    private sealed class Agent(bool reads, string answer, bool refuse = false) : IAssistantChatFactory
    {
        public List<Chat> Chats { get; } = [];
        public AiProvider Provider => AiProviders.Claude;
        public bool ReadsImages => reads;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            var chat = new Chat(answer, refuse);
            Chats.Add(chat);
            return chat;
        }

        public sealed class Chat(string answer, bool refuse) : IAssistantChat, IDisposable
        {
            public List<(string Text, AssistantImage Image)> Photos { get; } = [];
            public bool Disposed { get; private set; }

            public void AddUser(string text) { }

            public void AddUser(string text, AssistantImage image) => Photos.Add((text, image));

            public Task<AssistantStep> NextAsync(CancellationToken ct) => refuse
                ? throw new AssistantImageException("This model does not take this photo.")
                : Task.FromResult(new AssistantStep(answer, [], new AssistantUsage(10, 1, 0, 0), false));

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }

            public void Dispose() => Disposed = true;
        }
    }
}
