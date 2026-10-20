using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Pictures in Workspace Controller: sent from the phone, read from Claude's transcript, shown again on the phone.</summary>
public sealed class WorkspaceImageTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-img-");
    private readonly FakeSyncServer _server = new();
    private readonly List<IDisposable> _owned = [];
    private readonly string _pipe = "Helm.Workspace.Test." + Guid.NewGuid().ToString("N");
    private readonly MemorySynced<WorkspaceHostRecord> _hosts = new();
    private readonly MemorySynced<WorkspaceSessionRecord> _sessions = new();
    private readonly MemorySynced<WorkspaceCommand> _commands = new();
    private readonly MemorySynced<WorkspaceResultRecord> _results = new();
    private WorkspaceEngine? _engine;
    private WorkspaceRelay? _relay;
    private FakeBridge? _bridge;

    private BlobStore Blobs(string name)
    {
        var paths = new HelmPaths(Path.Combine(_dir.FullName, name));
        var db = new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local);
        _owned.Add(db);
        return new BlobStore(db, _server.Connect(), paths.SyncBlobsDirectory);
    }

    /// <summary>A photo as the phone sends it: imported as a blob and uploaded.</summary>
    private static async Task<(WorkspaceImage Image, byte[] Bytes)> PhonePhotoAsync(BlobStore phone, int size = 3000)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        var blob = await phone.ImportAsync(new MemoryStream(bytes));
        Assert.True(await phone.UploadPendingAsync(CancellationToken.None));
        return (new WorkspaceImage { Key = WorkspaceImages.KeyOf(bytes), MediaType = "image/jpeg", Width = 1200, Height = 900, Blob = blob }, bytes);
    }

    private string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(_dir.FullName, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string UserLine(object content, string at = "2026-10-19T07:00:00Z") =>
        JsonSerializer.Serialize(new { type = "user", timestamp = at, message = new { role = "user", content } });

    private static string AnswerLine(string text) =>
        JsonSerializer.Serialize(new { type = "assistant", timestamp = "2026-10-19T07:00:05Z", message = new { model = "claude-opus-5-5", content = new[] { new { type = "text", text } } } });

    private static object ImageBlock(string base64, string mediaType = "image/png") => new { type = "image", source = new { type = "base64", media_type = mediaType, data = base64 } };

    // ---- Claude's transcript

    [Fact]
    public void A_picture_in_a_prompt_is_read_as_a_picture_with_its_data()
    {
        var base64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(500));
        var path = WriteTranscript(UserLine(new object[] { ImageBlock(base64), new { type = "text", text = "What is wrong here?" } }), AnswerLine("A typo."));

        var view = TranscriptReader.ReadView(path);

        var prompt = view.Items[0];
        Assert.Equal("What is wrong here?", prompt.Text);
        var image = Assert.Single(prompt.Images);
        Assert.Equal(WorkspaceImages.KeyOf(base64), image.Key);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(base64, view.ImageData[image.Key]);
        // A prompt that is only a picture is still a prompt.
        var only = TranscriptReader.ReadView(WriteTranscript(UserLine(new object[] { ImageBlock(base64) })));
        Assert.Equal("", Assert.Single(only.Items).Text);
        Assert.Single(only.Items[0].Images);
    }

    [Fact]
    public void A_picture_line_typed_into_vs_code_shows_as_its_picture()
    {
        var key = new string('a', 32);
        var text = "Look at this\n\n" + WorkspaceImages.Marker(@"C:\Users\me\AppData\Local\Helm\images\" + key + ".jpg");
        var view = TranscriptReader.ReadView(WriteTranscript(UserLine(text)));

        var prompt = Assert.Single(view.Items);
        Assert.Equal("Look at this", prompt.Text);
        Assert.Equal(key, Assert.Single(prompt.Images).Key);
        var (shown, keys) = WorkspaceImages.SplitMarkers(text);
        Assert.Equal("Look at this", shown);
        Assert.Equal([key], keys);
    }

    [Fact]
    public void Pictures_that_fill_the_tail_do_not_hide_the_steps_before_them()
    {
        var lines = new List<string>();
        for (var i = 0; i < 20; i++) lines.Add(UserLine($"Step {i}"));
        // Two large pictures at the end: more than the usual tail on their own.
        var big = Convert.ToBase64String(RandomNumberGenerator.GetBytes(300_000));
        lines.Add(UserLine(new object[] { ImageBlock(big), ImageBlock(big + "AA") }));
        lines.Add(UserLine(new object[] { ImageBlock(big + "BB"), new { type = "text", text = "And this one" } }));

        var view = TranscriptReader.ReadView(WriteTranscript([.. lines]));

        Assert.Equal("And this one", view.Items[^1].Text);
        Assert.Contains(view.Items, i => i.Text == "Step 19");
        Assert.True(view.Items.Count >= TranscriptReader.MinItems);
    }

    [Fact]
    public void Steps_with_the_same_pictures_are_equal_whichever_list_holds_them()
    {
        var blob = new BlobRef("b1", 10, BlobStore.ChunkSize, 1, [1, 2, 3]);
        var a = new TranscriptItem { Kind = TranscriptItemKind.User, Text = "x", Images = [new WorkspaceImage { Key = "k", Blob = blob }] };
        var b = new TranscriptItem { Kind = TranscriptItemKind.User, Text = "x", Images = [new WorkspaceImage { Key = "k", Blob = blob with { Key = [1, 2, 3] } }] };
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Images = [] });
        // A record read back from sync is the same content: the PC does not write it again.
        var copy = JsonSerializer.Deserialize<TranscriptItem>(JsonSerializer.Serialize(a));
        Assert.Equal(a, copy);
    }

    // ---- Sending

    [Fact]
    public void A_message_with_pictures_puts_them_before_its_words()
    {
        var line = HeadlessClaude.Message("Fix it", [("image/jpeg", "QUJD")]);
        using var doc = JsonDocument.Parse(line);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("QUJD", content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("image/jpeg", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("Fix it", content[1].GetProperty("text").GetString());
        // Words alone stay a plain string, as before.
        Assert.Equal("Hi", JsonDocument.Parse(HeadlessClaude.Message("Hi", [])).RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_photo_for_a_session_outside_vs_code_reaches_claude_inside_the_message()
    {
        var phone = Blobs("phone");
        var images = new WorkspaceImageStore(Blobs("pc"), Path.Combine(_dir.FullName, "pc-images"), NullLogger.Instance);
        var claude = new WorkspaceEngineFixesTests.FakeClaude();
        var settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc"))).Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _engine = WorkspaceEngine.Start(settings, new HelmPaths(_dir.FullName), new PromptSender(new NoKeys(), NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Path.Combine(_dir.FullName, "projects"), headless: new HeadlessClaude(claude, NullLogger.Instance), images: images);
        Assert.True(_engine.TakesImages);
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "cli")).FullName;
        await _engine.HandleHookAsync(new ClaudeHookEvent { Event = ClaudeHookEvent.SessionStart, SessionId = "s1", Cwd = folder });
        var (photo, bytes) = await PhonePhotoAsync(phone);

        var run = _engine.RunAsync(new WorkspaceCommand { MachineId = _engine.MachineId, Kind = WorkspaceCommandKind.Prompt, SessionId = "s1", Folder = folder, Text = "What is this?", Images = [photo] },
            CancellationToken.None);
        await WorkspaceBridgeTests.WaitAsync(() => claude.Last);
        using var doc = JsonDocument.Parse((await claude.Last!.ReadInputAsync())!);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        var sent = content[0].GetProperty("source").GetProperty("data").GetString()!;
        Assert.Equal(bytes, Convert.FromBase64String(sent));
        // Claude's transcript will hold the same base64: its key is the photo's, so it is never uploaded again.
        Assert.Equal(photo.Key, WorkspaceImages.KeyOf(sent));
        Assert.Equal("What is this?", content[1].GetProperty("text").GetString());
        // Claude reports the words alone in its hook.
        await _engine.HandleHookAsync(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = "s1", Cwd = folder, Prompt = "What is this?" });
        Assert.Equal(WorkspaceCommandStatus.Received, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        var shown = images.Resolve([new TranscriptItem { Kind = TranscriptItemKind.User, Images = [new WorkspaceImage { Key = photo.Key }] }]);
        Assert.Equal(photo.Blob!.Id, shown[0].Images[0].Blob?.Id);
    }

    [Fact]
    public async Task A_photo_for_vs_code_goes_in_as_a_file_claude_may_open_without_asking()
    {
        var phone = Blobs("phone");
        var images = new WorkspaceImageStore(Blobs("pc"), Path.Combine(_dir.FullName, "pc-images"), NullLogger.Instance);
        var keyboard = new ClaudeKeyboard();
        var settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc"))).Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        settings.Update(s => s.ApproveFromPhone = PhoneApprovals.Always);
        _engine = WorkspaceEngine.Start(settings, new HelmPaths(_dir.FullName), new PromptSender(keyboard, NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Path.Combine(_dir.FullName, "no-projects"), images: images);
        _engine.PhoneLinked = true;
        keyboard.Registry = _engine.Registry;
        _bridge = new FakeBridge(_pipe);
        await _bridge.ConnectAsync("app", [@"c:\work\app"]);
        await WorkspaceBridgeTests.WaitAsync(() => _engine.Windows().SingleOrDefault());
        _engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = "s1", Cwd = @"c:\work\app", Prompt = "Start" });
        var (photo, bytes) = await PhonePhotoAsync(phone);

        var result = await _engine.RunAsync(new WorkspaceCommand
        {
            MachineId = _engine.MachineId, Kind = WorkspaceCommandKind.Prompt, SessionId = "s1", Folder = @"c:\work\app", Text = "Match this design", Images = [photo],
        }, CancellationToken.None);

        Assert.Equal(WorkspaceCommandStatus.Received, result.Status);
        var file = Path.Combine(images.Folder, photo.Key + ".jpg");
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal("Match this design\n\n" + WorkspaceImages.Marker(file), keyboard.LastSubmitted);
        // Claude opens it: allowed at once, nothing is held for the phone.
        var read = await _engine.HandleHookAsync(Permission("Read", file));
        Assert.Contains("\"allow\"", read);
        Assert.Null(_engine.Registry.Find("s1")!.Pending);
        // Any other file is asked as usual.
        var other = _engine.HandleHookAsync(Permission("Read", Path.Combine(_dir.FullName, "secret.txt")));
        await WorkspaceBridgeTests.WaitAsync(() => _engine.Registry.Find("s1")?.Pending);
        Assert.False(other.IsCompleted);
        // Only words: a picture alone is a message too.
        var alone = await _engine.RunAsync(new WorkspaceCommand { MachineId = _engine.MachineId, Kind = WorkspaceCommandKind.Prompt, SessionId = "s1", Folder = @"c:\work\app", Images = [photo] },
            CancellationToken.None);
        Assert.NotEqual(WorkspaceCommandStatus.Failed, alone.Status);
        Assert.Equal(WorkspaceImages.Marker(file), keyboard.LastSubmitted);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_fetched_fails_the_message_with_a_reason()
    {
        var images = new WorkspaceImageStore(Blobs("pc"), Path.Combine(_dir.FullName, "pc-images"), NullLogger.Instance);
        var claude = new WorkspaceEngineFixesTests.FakeClaude();
        var settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc"))).Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _engine = WorkspaceEngine.Start(settings, new HelmPaths(_dir.FullName), new PromptSender(new NoKeys(), NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Path.Combine(_dir.FullName, "projects"), headless: new HeadlessClaude(claude, NullLogger.Instance), images: images);
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "cli")).FullName;
        await _engine.HandleHookAsync(new ClaudeHookEvent { Event = ClaudeHookEvent.SessionStart, SessionId = "s1", Cwd = folder });
        var missing = new WorkspaceImage { Key = "k", Blob = new BlobRef("never-uploaded", 10, BlobStore.ChunkSize, 1, RandomNumberGenerator.GetBytes(32)) };

        var result = await _engine.RunAsync(new WorkspaceCommand { MachineId = _engine.MachineId, Kind = WorkspaceCommandKind.Prompt, SessionId = "s1", Folder = folder, Text = "x", Images = [missing] },
            CancellationToken.None);

        Assert.Equal(WorkspaceCommandStatus.Failed, result.Status);
        Assert.Contains("picture", result.Error);
        Assert.Null(claude.Last);
    }

    // ---- Shown again on the phone

    [Fact]
    public async Task A_picture_pasted_on_the_pc_is_uploaded_once_and_then_shown_on_the_phone()
    {
        var pcBlobs = Blobs("pc");
        var images = new WorkspaceImageStore(pcBlobs, Path.Combine(_dir.FullName, "pc-images"), NullLogger.Instance);
        var settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc"))).Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _engine = WorkspaceEngine.Start(settings, new HelmPaths(_dir.FullName), new PromptSender(new NoKeys(), NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Path.Combine(_dir.FullName, "no-projects"), images: images);
        _bridge = new FakeBridge(_pipe);
        await _bridge.ConnectAsync("app", [@"c:\work\app"]);
        await WorkspaceBridgeTests.WaitAsync(() => _engine.Windows().SingleOrDefault());
        _relay = new WorkspaceRelay(_engine, _hosts, _sessions, _commands, _results, "Desk PC", null, NullLogger.Instance, images: images);
        var bytes = RandomNumberGenerator.GetBytes(2000);
        var base64 = Convert.ToBase64String(bytes);
        var transcript = WriteTranscript(UserLine(new object[] { ImageBlock(base64), new { type = "text", text = "Why?" } }), AnswerLine("Because."));
        _engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = "s1", Cwd = @"c:\work\app", TranscriptPath = transcript, Prompt = "Why?" });

        // Until its blob is on the server the phone sees the picture without one (the record is never held back).
        var first = await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { Items.Count: > 0 } r ? r : null);
        Assert.Null(first.Items[0].Images[0].Blob);
        Assert.True(_hosts.Get(_engine.MachineId)!.TakesImages);
        await WorkspaceBridgeTests.WaitAsync(() => pcBlobs.PendingIds().Count == 1 ? (object)true : null);
        Assert.True(await pcBlobs.UploadPendingAsync(CancellationToken.None));

        var shown = await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1")?.Items[0].Images[0].Blob);
        Assert.Equal(bytes, await Blobs("phone").ReadAllAsync(shown));
        Assert.Equal([shown.Id], WorkspaceSync.ImagesOf(_sessions.Get("s1")!));
        // Read again later (the transcript grew): known by its key, not uploaded a second time.
        File.AppendAllLines(transcript, [AnswerLine("More.")]);
        _engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.Stop, SessionId = "s1", Cwd = @"c:\work\app", TranscriptPath = transcript, LastAssistantMessage = "More." });
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { State: SessionState.Done } r ? r : null);
        Assert.Empty(pcBlobs.PendingIds());
        Assert.Equal(shown.Id, _sessions.Get("s1")!.Items[0].Images[0].Blob?.Id);
    }

    [Fact]
    public async Task The_phone_attaches_photos_sends_them_and_shows_them_in_the_timeline()
    {
        var phoneBlobs = Blobs("phone");
        var images = new WorkspaceImageStore(Blobs("pc"), Path.Combine(_dir.FullName, "pc-images"), NullLogger.Instance);
        var keyboard = new ClaudeKeyboard();
        var pcSettings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc"))).Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _engine = WorkspaceEngine.Start(pcSettings, new HelmPaths(_dir.FullName), new PromptSender(keyboard, NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Path.Combine(_dir.FullName, "no-projects"), images: images);
        keyboard.Registry = _engine.Registry;
        _bridge = new FakeBridge(_pipe);
        await _bridge.ConnectAsync("app", [@"c:\work\app"]);
        await WorkspaceBridgeTests.WaitAsync(() => _engine.Windows().SingleOrDefault());
        _relay = new WorkspaceRelay(_engine, _hosts, _sessions, _commands, _results, "Desk PC", null, NullLogger.Instance, images: images);
        _engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = "s1", Cwd = @"c:\work\app", Prompt = "Start" });
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1"));
        var ui = new SerialUi();
        using var phoneSettings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "phone")));
        var picker = new Picker();
        var phone = ui.Run(() => new WorkspaceRemoteViewModel(_hosts, _sessions, _commands, _results, phoneSettings, ui, blobs: phoneBlobs, picker: picker));
        Assert.True(phone.CanAttach);

        await ui.Run(() => phone.PickImagesCommand.ExecuteAsync(null));
        await ui.Run(() => phone.TakePhotoCommand.ExecuteAsync(null));
        Assert.Equal(2, phone.Attachments.Count);
        Assert.Equal(WorkspaceImages.MaxPerMessage, picker.AskedFor);
        // A picture taken out of the draft is forgotten on the phone at once.
        var dropped = phone.Attachments[1];
        ui.Run(() => phone.RemoveAttachmentCommand.Execute(dropped));
        Assert.DoesNotContain(dropped.Image.Blob!.Id, phoneBlobs.PendingIds());
        var photo = phone.Attachments[0].Image;
        // A picture alone can be sent.
        Assert.True(phone.SendCommand.CanExecute(null));
        // It uploads while you type (the sync engine would hold the command until it is on the server).
        Assert.True(await phoneBlobs.UploadPendingAsync(CancellationToken.None));

        ui.Run(() =>
        {
            phone.Prompt = "Like this";
            phone.SendCommand.Execute(null);
            Assert.Empty(phone.Attachments);
            var sending = phone.Timeline.Last(r => r.IsUser);
            Assert.Equal("Like this", sending.Text);
            Assert.Equal(photo.Key, Assert.Single(sending.Images).Key);
        });
        var command = Assert.Single(_commands.All()).Value;
        Assert.Equal(photo.Blob!.Id, Assert.Single(command.Images).Blob?.Id);
        await WorkspaceBridgeTests.WaitAsync(() => phone.IsSending ? null : (object)true);
        Assert.Equal("✓ Claude received it", phone.SendStatus);
        Assert.Contains(photo.Key + ".jpg]", keyboard.LastSubmitted);

        // Claude's transcript names the file: the timeline shows the photo, from the phone's own copy.
        var transcript = WriteTranscript(UserLine("Start"), UserLine(keyboard.LastSubmitted!, "2099-01-01T00:00:00Z"));
        _engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.Stop, SessionId = "s1", Cwd = @"c:\work\app", TranscriptPath = transcript, LastAssistantMessage = "Done." });
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { Items.Count: 2 } r ? r : null);
        var row = await WorkspaceBridgeTests.WaitAsync(() => phone.Timeline.FirstOrDefault(r => r.IsUser && r.HasImages));
        Assert.Equal("Like this", row.Text);
        Assert.Equal(photo.Blob!.Id, row.Images[0].Blob?.Id);
        ui.Run(() => phone.ViewImageCommand.Execute(row.Images[0]));
        Assert.True(phone.IsViewingImage);
        Assert.True(phone.HandleBack());
        Assert.False(phone.IsViewingImage);

        // A PC of 0.47 takes no pictures: no 📎 there.
        _hosts.Upsert(_engine.MachineId, _hosts.Get(_engine.MachineId)! with { TakesImages = false });
        await WorkspaceBridgeTests.WaitAsync(() => phone.ImagesSupported ? null : (object)true);
        Assert.False(phone.CanAttach);
    }

    private sealed class Picker : IWorkspaceImagePicker
    {
        public int AskedFor { get; private set; }

        public Task<PickedImage?> TakePhotoAsync(CancellationToken ct) => Task.FromResult<PickedImage?>(new PickedImage(RandomNumberGenerator.GetBytes(900), "image/jpeg", 800, 600));

        public Task<IReadOnlyList<PickedImage>> PickAsync(int max, CancellationToken ct)
        {
            AskedFor = max;
            return Task.FromResult<IReadOnlyList<PickedImage>>([new PickedImage(RandomNumberGenerator.GetBytes(700), "image/jpeg", 600, 800)]);
        }
    }

    private static ClaudeHookEvent Permission(string tool, string file) => new()
    {
        Event = ClaudeHookEvent.PermissionRequest, SessionId = "s1", Cwd = @"c:\work\app", ToolName = tool,
        ToolInputJson = JsonSerializer.Serialize(new { file_path = file }),
    };

    public async ValueTask DisposeAsync()
    {
        if (_relay is not null) await _relay.DisposeAsync();
        if (_bridge is not null) await _bridge.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        foreach (var owned in _owned) owned.Dispose();
        try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoKeys : IPromptKeyboard
    {
        public IReadOnlyList<(nint Handle, string Title)> TopWindows() => [];
        public nint Foreground() => 0;
        public bool ForceActivate(nint hwnd) => false;
        public void Activate(nint hwnd) { }
        public bool ModifierDown() => false;
        public void Type(string text) { }
        public void NewLine() { }
        public void Submit(bool ctrl) { }
        public Task Pause(int milliseconds, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> WaitFor(Task task, int milliseconds, CancellationToken ct) => Task.FromResult(task.IsCompleted);
    }

    /// <summary>A pretend VS Code: Enter fires Claude's UserPromptSubmit hook with what was typed.</summary>
    private sealed class ClaudeKeyboard : IPromptKeyboard
    {
        private string _typed = "";
        private nint _foreground;

        public SessionRegistry? Registry { get; set; }
        public string? LastSubmitted { get; private set; }

        public IReadOnlyList<(nint Handle, string Title)> TopWindows() => [(42, "app - Visual Studio Code")];
        public nint Foreground() => _foreground;
        public bool ForceActivate(nint hwnd) { _foreground = hwnd; return true; }
        public void Activate(nint hwnd) { }
        public bool ModifierDown() => false;
        public void Type(string text) => _typed += text;
        public void NewLine() => _typed += "\n";

        public void Submit(bool ctrl)
        {
            LastSubmitted = _typed;
            _typed = "";
            Registry!.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = "s1", Cwd = @"c:\work\app", Prompt = LastSubmitted });
        }

        public Task Pause(int milliseconds, CancellationToken ct) => Task.CompletedTask;

        public async Task<bool> WaitFor(Task task, int milliseconds, CancellationToken ct)
        {
            await Task.WhenAny(task, Task.Delay(50, ct));
            return task.IsCompleted;
        }
    }
}
