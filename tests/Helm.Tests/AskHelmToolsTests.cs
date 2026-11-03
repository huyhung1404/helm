using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.Wallet;
using Helm.Modules.Workspace;
using static Helm.Tests.AskHelmScript;

namespace Helm.Tests;

/// <summary>
/// Ask Helm's other tools: directions and apps on the phone, a mission made from one sentence, quick ideas, notes found
/// by meaning, and a request typed into Claude Code on the PC.
/// </summary>
public sealed class AskHelmToolsTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    // ---- A4: directions and apps ---------------------------------------------------------------------------------

    private static readonly PhoneApps.App[] Apps =
    [
        new("Grab", "com.grabtaxi.passenger"), new("YouTube", "com.google.android.youtube"), new("YouTube Music", "com.google.android.apps.youtube.music"),
        new("Zalo", "com.zing.zalo"), new("Maps", "com.google.android.apps.maps"),
    ];

    [Theory]
    [InlineData("grab", "Grab")]
    [InlineData("You Tube", "YouTube")]
    [InlineData("ứng dụng Zalo", "Zalo")]
    [InlineData("zing", "Zalo")]
    public void An_app_is_found_by_the_name_the_user_said(string said, string app) =>
        Assert.Equal(app, Assert.Single(PhoneApps.Match(said, Apps)).Label);

    [Fact]
    public void Several_apps_that_fit_are_all_returned_so_the_user_is_asked()
    {
        Assert.Equal(["YouTube", "YouTube Music"], PhoneApps.Match("you", Apps).Select(a => a.Label));
        Assert.Empty(PhoneApps.Match("shopee", Apps));
    }

    [Fact]
    public void Directions_open_turn_by_turn_on_a_motorbike_or_the_web_link()
    {
        Assert.Equal("google.navigation:q=36%20Ho%C3%A0ng%20C%E1%BA%A7u&mode=l", PhoneMaps.Navigation("36 Hoàng Cầu", null));
        Assert.Null(PhoneMaps.Navigation("Hồ Gươm", "transit"));
        Assert.EndsWith("&travelmode=walking", PhoneMaps.Directions("Hồ Gươm", "walk"));
    }

    [Fact]
    public async Task Directions_run_on_the_phone_at_once()
    {
        var phone = new FakePhone();
        var script = new AskHelmScript(Step(Call("phone_navigate", """{"destination":"36 Hoàng Cầu","summary":"Chỉ đường tới 36 Hoàng Cầu"}""")));

        var reply = await script.Say("Đường tới 36 Hoàng Cầu", [], [phone]);

        Assert.Equal("phone_navigate", Assert.Single(phone.Calls).Tool);
        Assert.Equal("Chỉ đường tới 36 Hoàng Cầu", Assert.Single(reply.Done).Summary);
        Assert.All(script.Offered[0], t => Assert.Equal("phone", ToolScope.GroupOf(t.Name)));
    }

    [Fact]
    public async Task On_a_PC_the_new_phone_tools_are_handed_back_and_a_split_comes_back_one_record_each()
    {
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
        var agent = new AskHelmScript(Step(
            Call("phone_open_app", """{"app":"Grab","summary":"Mở Grab"}"""),
            Call("wallet_split_bill", """{"total":"600k","description":"Lẩu","owing":["Nam"],"summary":"Chia lẩu"}""")));
        using var pc = new LocalAgentRoute("PC-HUNG", "Claude Code", agent, [new WalletShareTools(debts, wallet)], _ => true, () => null, _time);

        var result = await pc.RunAsync(new AiJob(AiFeatureIds.AskHelm, "c1", "mở Grab, lẩu 600k chia đôi với Nam", ["wallet_split_bill"],
            ["phone_open_app", "phone_navigate", "phone_share_text"], "claude-haiku-5-5", "low"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Contains(agent.Offered[0], t => t.Name == "phone_share_text");
        Assert.Equal("phone_open_app", Assert.Single(result.PhoneActions).Tool);
        Assert.Equal(["Chi 300.000đ · Lẩu (phần Anh)", "Nam nợ 300.000đ · Lẩu"], result.Done.Select(d => d.Summary));

        // Undo of one record, from the phone, takes back only that one.
        await pc.UndoAsync(result.Done[1], CancellationToken.None);
        Assert.Equal(0m, debts.Balance("Nam"));
        Assert.Single(wallet.Transactions());
    }

    // ---- P2: a mission from one sentence -------------------------------------------------------------------------

    private const string Aptis = """
        {"title":"Đỗ Aptis B1","deadline":"2026-11-15","start":true,"summary":"Mission Đỗ Aptis B1 · hạn 15/11",
         "phases":[{"title":"Ôn","steps":[{"title":"Ngữ pháp","estimateDays":3},{"title":"Thi thử","estimateDays":1}]},
                   {"title":"Thi","steps":[{"title":"Thi Aptis B1","estimateDays":1}]}]}
        """;

    [Fact]
    public async Task A_mission_is_created_at_once_with_its_steps_and_Undo_removes_it()
    {
        var store = new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>(), _time);
        var script = new AskHelmScript(Step(Call("mission_create", Aptis)));

        var reply = await script.Say("Đỗ Aptis B1 ngày 15/11", [new MissionsMcpTools(store)]);

        Assert.Equal(AssistantLoop.DoneText, reply.Text);
        Assert.Contains(script.Offered[0], t => t.Name == "mission_create");
        var mission = Assert.Single(store.Missions());
        Assert.Equal(MissionStatus.Active, mission.Value.Status);
        Assert.Equal(new DateOnly(2026, 11, 15), mission.Value.Deadline);
        Assert.Equal(["Ngữ pháp", "Thi thử", "Thi Aptis B1"], store.Steps(mission.Id).Select(s => s.Value.Title));

        await Assert.Single(reply.Done).UndoAsync(CancellationToken.None);
        Assert.Empty(store.Missions());
        Assert.Empty(store.Steps(mission.Id));
    }

    [Fact]
    public async Task A_step_completed_from_the_card_can_be_undone()
    {
        var store = new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>(), _time);
        var tools = new MissionsMcpTools(store).Tools.ToDictionary(t => t.Name);
        await tools["mission_create"].Run(JsonDocument.Parse(Aptis).RootElement, default);
        var id = Assert.Single(store.Missions()).Id;

        var done = JsonSerializer.SerializeToElement(await tools["mission_complete_step"].Run(JsonDocument.Parse($$"""{"mission":"{{id}}"}""").RootElement, default));
        Assert.Equal("Thi thử", store.CurrentStep(id)!.Value.Title);

        await tools["mission_complete_step"].Undo!(done, default);
        Assert.Equal("Ngữ pháp", store.CurrentStep(id)!.Value.Title);
    }

    // ---- J8 and O4: ideas and notes by meaning --------------------------------------------------------------------

    [Fact]
    public async Task An_idea_is_a_note_tagged_with_idea_and_its_project_from_the_tags_in_use()
    {
        var notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        notes.Add("Boss fight", "Ba pha, mỗi pha một màu #game");
        notes.Add("", "Issue #12 vẫn còn #helm");
        var tools = new NotesMcpTools(notes);
        var create = tools.Tools.Single(t => t.Name == "notes_create");

        // The model sees how the user tags notes already (a number is not a tag).
        Assert.Contains("#game (1)", create.Description);
        Assert.Contains("#helm (1)", create.Description);
        Assert.DoesNotContain("#12", create.Description);

        var script = new AskHelmScript(Step(Call("notes_create",
            """{"text":"Ý tưởng game: kẻ địch học theo cách chơi của người chơi","tags":["idea","Game"],"summary":"Ghi ý tưởng game"}""")));
        var reply = await script.Say("ý tưởng game: kẻ địch học theo cách chơi của người chơi", [tools]);

        var saved = notes.Notes().First(n => n.Value.Body.StartsWith("Ý tưởng game", StringComparison.Ordinal)).Value;
        Assert.EndsWith("\n\n#idea #game", saved.Body);
        Assert.True(Assert.Single(reply.Done).CanUndo);
        Assert.Equal("#game", NoteTags.Append("x #game", ["game"])[^5..]);
    }

    [Fact]
    public async Task Notes_are_found_by_alternative_words_the_ones_with_more_of_them_first_then_the_newer()
    {
        var notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        var old = notes.Add("VPS", "Server sập lúc 2h, lỗi 500 do hết RAM");
        _time.Advance(TimeSpan.FromDays(20));
        var fresh = notes.Add("Deploy", "Server error khi deploy");
        notes.Add("Đi chợ", "Mua rau");
        var search = new NotesMcpTools(notes).Tools.Single(t => t.Name == "notes_search");

        var found = JsonSerializer.SerializeToElement(await search.Run(
            JsonDocument.Parse("""{"any":["lỗi server","server error","sập","crash","500"]}""").RootElement, default));

        Assert.Equal([old, fresh], found.EnumerateArray().Select(n => n.GetProperty("id").GetString()));
        Assert.Contains("several words", search.Description);
    }

    [Fact]
    public void Among_notes_with_as_many_matches_the_newer_comes_first()
    {
        var notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        var old = notes.Add("A", "server down");
        _time.Advance(TimeSpan.FromDays(30));
        var fresh = notes.Add("B", "server down");

        Assert.Equal([fresh, old], NoteRecall.Rank(notes.Notes(), ["sap", "server"], _time.Now).Select(n => n.Id));
    }

    // ---- K1: a request typed into Claude Code ---------------------------------------------------------------------

    private readonly MemorySynced<WorkspaceHostRecord> _hosts = new();
    private readonly MemorySynced<WorkspaceSessionRecord> _sessions = new();
    private readonly MemorySynced<WorkspaceCommand> _commands = new();
    private readonly MemorySynced<WorkspaceResultRecord> _results = new();

    private WorkspaceAskTools Workspace()
    {
        _hosts.Upsert("pc1", new WorkspaceHostRecord
        {
            MachineId = "pc1", MachineName = "PC-HUNG", Running = true, LastSeen = _time.Now,
            Windows =
            [
                new HostWindowInfo { Folder = @"C:\Project\Helm", Name = "Helm" },
                new HostWindowInfo { Folder = @"C:\Project\WebAdmin", Name = "WebAdmin" },
                new HostWindowInfo { Folder = @"C:\tmp\hw-ask", Name = "hw-ask" },
            ],
        });
        _hosts.Upsert("pc2", new WorkspaceHostRecord { MachineId = "pc2", MachineName = "OLD-PC", Running = true, LastSeen = _time.Now - TimeSpan.FromHours(1) });
        _sessions.Upsert("s1", new WorkspaceSessionRecord { MachineId = "pc1", SessionId = "s1", WindowFolder = @"C:\Project\Helm", Folder = @"C:\Project\Helm", Title = "Fix the sync", State = SessionState.Done });
        _sessions.Upsert("s2", new WorkspaceSessionRecord { MachineId = "pc1", SessionId = "s2", WindowFolder = @"C:\Project\WebAdmin", Folder = @"C:\Project\WebAdmin", Title = "Deploy", State = SessionState.Working });
        _sessions.Upsert("s3", new WorkspaceSessionRecord { MachineId = "pc1", SessionId = "s3", WindowFolder = @"C:\Project\WebAdmin", Folder = @"C:\Project\WebAdmin", Title = "Backups", State = SessionState.Idle });
        _sessions.Upsert("s4", new WorkspaceSessionRecord { MachineId = "pc2", SessionId = "s4", WindowFolder = @"D:\Helm", Folder = @"D:\Helm", Title = "Old", State = SessionState.Idle });
        return new WorkspaceAskTools(_hosts, _sessions, _commands, _results, time: _time);
    }

    [Fact]
    public async Task A_spoken_request_goes_to_the_session_of_the_project_named_and_Undo_withdraws_it_until_the_PC_takes_it()
    {
        var script = new AskHelmScript(Step(Call("workspace_send", """{"to":"Helm","text":"thêm test cho Ask Helm","summary":"Gửi Claude Code (Helm): thêm test cho Ask Helm"}""")));

        var reply = await script.Say("gửi cho Claude Code ở Helm: thêm test cho Ask Helm", [Workspace()]);

        Assert.All(script.Offered[0], t => Assert.Equal("workspace", ToolScope.GroupOf(t.Name)));
        var command = Assert.Single(_commands.All()).Value;
        Assert.Equal(WorkspaceCommandKind.Prompt, command.Kind);
        Assert.Equal(("pc1", "s1", "thêm test cho Ask Helm"), (command.MachineId, command.SessionId, command.Text));

        await Assert.Single(reply.Done).UndoAsync(CancellationToken.None);
        Assert.Empty(_commands.All());
    }

    [Fact]
    public async Task A_request_the_PC_already_took_is_not_taken_back()
    {
        var send = Workspace().Tools.Single(t => t.Name == "workspace_send");
        var sent = JsonSerializer.SerializeToElement(await send.Run(JsonDocument.Parse("""{"to":"helm","text":"go"}""").RootElement, default));
        var id = sent.GetProperty("id").GetString()!;
        _results.Upsert(id, new WorkspaceResultRecord { CommandId = id, MachineId = "pc1", Status = WorkspaceCommandStatus.Received });

        await Assert.ThrowsAsync<McpToolException>(() => send.Undo!(sent, default));
        Assert.Single(_commands.All());
    }

    [Fact]
    public async Task Several_sessions_that_fit_are_listed_for_the_user_to_choose_and_nothing_is_sent()
    {
        var send = Workspace().Tools.Single(t => t.Name == "workspace_send");

        var ex = await Assert.ThrowsAsync<McpToolException>(() => send.Run(JsonDocument.Parse("""{"to":"WebAdmin","text":"status?"}""").RootElement, default));

        Assert.Contains("Deploy (working)", ex.Message);
        Assert.Contains("Backups (idle)", ex.Message);
        Assert.Empty(_commands.All());
    }

    [Fact]
    public async Task A_window_without_a_session_opens_a_new_one_and_an_offline_PC_is_left_out()
    {
        var tools = Workspace().Tools.ToDictionary(t => t.Name);

        await tools["workspace_send"].Run(JsonDocument.Parse("""{"to":"hw-ask","text":"chạy test"}""").RootElement, default);
        var list = JsonSerializer.Serialize(await tools["workspace_sessions"].Run(JsonDocument.Parse("{}").RootElement, default));

        var command = Assert.Single(_commands.All()).Value;
        Assert.Equal(WorkspaceCommandKind.NewSession, command.Kind);
        Assert.Equal(@"C:\tmp\hw-ask", command.Folder);
        Assert.DoesNotContain("OLD-PC", list);
        Assert.Contains("Fix the sync", list);
    }

    [Theory]
    [InlineData("gửi cho Claude Code ở Helm: thêm test", "workspace")]
    [InlineData("Đường tới 36 Hoàng Cầu", "phone")]
    [InlineData("mở Grab", "phone")]
    [InlineData("Đỗ Aptis B1 ngày 15/11", "mission")]
    [InlineData("ý tưởng game: kẻ địch học theo người chơi", "notes")]
    [InlineData("Ghi chú về lỗi server hôm trước", "notes")]
    public void The_requests_carry_their_tools(string said, string group) => Assert.Contains(group, ToolScope.GroupsFor(said));

    [Fact]
    public void The_new_tools_are_Ask_Helms_and_each_change_can_be_undone()
    {
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
        var links = new LinkHub(new MemorySynced<HelmLink>(), () => [], _time);
        IMcpToolProvider[] providers =
        [
            new WalletShareTools(debts, wallet), new BillMcpTools(new BillBook(new MemorySynced<WalletBill>(), wallet, _time), wallet),
            new MissionsMcpTools(new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>(), _time)),
            new NotesMcpTools(new NotesStore(new MemorySynced<NoteItem>(), _time), links), Workspace(),
        ];
        var offered = new AskHelmScript().Assistant(providers).OfferedTools([new FakePhone()]).ToDictionary(t => t.Name);

        foreach (var name in new[] { "wallet_split_bill", "bill_add", "bill_pay", "mission_create", "mission_complete_step", "mission_skip_step", "mission_check_item", "notes_create", "workspace_send" })
            Assert.Equal(AssistantToolMode.Auto, offered[name].Mode);
        foreach (var name in new[] { "wallet_debt_reminder", "bills_list", "workspace_sessions", "notes_search" })
            Assert.Equal(AssistantToolMode.Read, offered[name].Mode);
        Assert.Contains("phone_navigate", offered.Keys);
        Assert.DoesNotContain("mission_replan", offered.Keys);
    }
}
