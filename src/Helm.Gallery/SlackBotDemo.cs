using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.SlackBot;
using Helm.Modules.SlackBot.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which Slack Report a Gallery entry shows.</summary>
internal enum SlackBotStage
{
    /// <summary>Connected, with DMs, a thread and mentions waiting (one not sorted yet).</summary>
    ToDo,
    /// <summary>The same, on the Worth knowing tab (by channel).</summary>
    Knowing,
    /// <summary>What was handled today, unfolded under the list.</summary>
    Handled,
    /// <summary>The reply sheet over the list.</summary>
    Reply,
    /// <summary>Claude Code cannot sort: the warning, messages shown as they are.</summary>
    SortProblem,
    /// <summary>Connected, nothing waits.</summary>
    AllClear,
    /// <summary>Not signed in to Slack yet.</summary>
    FirstRun,
    /// <summary>The settings sheet (the gear), connected.</summary>
    Settings,
    /// <summary>The settings sheet scrolled to its end (sorting, the list on screen, the AI signature).</summary>
    SettingsEnd,
    /// <summary>The settings sheet on the steps to connect (first run's button).</summary>
    SettingsConnect,
}

/// <summary>Which state of the PC's list that stays on screen a Gallery entry shows.</summary>
internal enum SlackListStage
{
    /// <summary>Open, with the to-do cards (see-through at rest).</summary>
    Open,
    /// <summary>Writing a reply in it.</summary>
    Reply,
    /// <summary>Folded to its badge with the count.</summary>
    Folded,
}

/// <summary>
/// The real Slack Report view over an inbox in a temporary folder, at a game studio. Nothing reaches Slack: the engine
/// never starts, shows itself as connected (<see cref="SlackBotEngine.ShowConnectedForPreview"/>) with no API, and the
/// tokens it keeps can never be read back (Reconnect stops at "cannot be read on this PC").
/// </summary>
internal static class SlackBotDemo
{
    public static Control Create(FakeModule module, SlackBotStage stage) => Build(module, stage).View;

    /// <summary>The view with its two view models (the UI tests drive them).</summary>
    public static (SlackBotView View, SlackBotInboxViewModel Inbox, SlackBotViewModel Settings) Build(FakeModule module, SlackBotStage stage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var paths = new HelmPaths(dir);
        var settings = new SettingsStoreFactory(paths);
        var protector = new UnreadableProtector();
        var tokens = new SlackTokens(paths, protector);
        var connected = stage is not (SlackBotStage.FirstRun or SlackBotStage.SettingsConnect);
        if (connected) tokens.Save(new SlackTokenPair("xoxp-gallery", "xapp-gallery"));
        var file = new SlackInboxFile(paths, protector, NullLogger<SlackInboxFile>.Instance);
        var engine = new SlackBotEngine(tokens, file, new SlackSorter(new GalleryAgent(stage == SlackBotStage.SortProblem)), settings,
            NullLogger<SlackBotEngine>.Instance);

        settings.Get<SlackBotSettings>(SlackBotIds.ModuleId).Update(s =>
        {
            s.AboutMe = "Mình là lead Unity client của game Bubble Pop. Lỗi build, ngày release và câu hỏi từ PO / QA thì cần mình.";
            s.Model = "sonnet";
            s.Channels["C01REL"] = SlackChannelMode.Reply;
            s.Channels["C02BUG"] = SlackChannelMode.Read;
            s.Channels["C03GEN"] = SlackChannelMode.Read;
            s.Channels["C04ART"] = SlackChannelMode.Read;
        });

        if (connected)
        {
            if (stage != SlackBotStage.AllClear) Seed(file.Inbox, unsorted: stage == SlackBotStage.SortProblem);
            engine.ShowConnectedForPreview(new SlackIdentity("U0HUNG", "hung.nh", "T0STUDIO", "https://bubble-studio.slack.com/"), "Hùng", Conversations);
            if (stage == SlackBotStage.SortProblem)
                engine.ShowStateForPreview(SlackBotState.Connected, "Connected as hung.nh.",
                    "Claude Code is not installed on this PC. Messages for you show unsorted until it is.");
        }
        else
        {
            engine.ShowStateForPreview(SlackBotState.NoTokens, "Connect your Slack app in the settings (the gear).");
        }

        var ui = new AvaloniaUi();
        var shell = new NoShell();
        var inbox = new SlackBotInboxViewModel(engine, ui, new NoLauncher(), shell, settings);
        var options = new SlackBotViewModel(module, engine, tokens, settings, ui, new NoLauncher(), shell);
        var view = new SlackBotView(module, inbox, options, new Host());

        switch (stage)
        {
            case SlackBotStage.Knowing:
                view.ShowKnowing();
                break;
            case SlackBotStage.Handled:
                view.AttachedToVisualTree += (_, _) => DispatcherTimer.RunOnce(view.ShowHandled, TimeSpan.FromMilliseconds(250));
                break;
            case SlackBotStage.Reply:
                inbox.StartReplyCommand.Execute(inbox.ToDo[0]);
                inbox.ReplyText = "Ok em, để anh xem log rồi báo lại trước 3h nhé.";
                break;
            case SlackBotStage.Settings or SlackBotStage.SettingsEnd or SlackBotStage.SettingsConnect:
                view.AttachedToVisualTree += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    view.OpenSettings(openConnection: stage == SlackBotStage.SettingsConnect);
                    if (stage == SlackBotStage.SettingsEnd) Dispatcher.UIThread.Post(() => ScrollSheetToEnd(view), DispatcherPriority.Background);
                }, TimeSpan.FromMilliseconds(250));
                break;
        }
        return (view, inbox, options);
    }

    /// <summary>
    /// The PC's list that stays on screen (the real view its window shows) over the same inbox, in the bottom-right corner
    /// of a screen: open, writing a reply, or folded to its badge.
    /// </summary>
    public static Control List(FakeModule module, SlackListStage stage) => BuildList(module, stage).Screen;

    /// <summary>The list with its inbox (the UI tests drive them).</summary>
    public static (Control Screen, SlackBotListView List, SlackBotInboxViewModel Inbox) BuildList(FakeModule module, SlackListStage stage,
        ISlackBotListHost? host = null)
    {
        var (_, inbox, _) = Build(module, SlackBotStage.ToDo);
        var list = new SlackBotListView(inbox, host ?? new ListHost(), Helm.ToolIcons.ToolIcons.Of(SlackBotIds.ModuleId), stage == SlackListStage.Folded)
        {
            Margin = new Avalonia.Thickness(12),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            RestOpacity = stage == SlackListStage.Open ? 0.85 : 1,
        };
        if (stage == SlackListStage.Reply)
        {
            inbox.StartReplyCommand.Execute(inbox.ToDo[0]);
            inbox.ReplyText = "Ok em, để anh xem log rồi báo lại trước 3h nhé.";
        }
        return (new Panel { Children = { new Helm.Core.Ui.Backdrop(), list } }, list, inbox);
    }

    private sealed class ListHost : ISlackBotListHost
    {
        public void Folded(bool folded) { }

        public void TakeKeyboard() { }

        public void ReturnKeyboard() { }

        public bool Drag(Avalonia.Input.PointerPressedEventArgs e) => false;

        public void BackToCorner() { }
    }

    /// <summary>The settings sheet's own scroller, at its end.</summary>
    private static void ScrollSheetToEnd(Control view)
    {
        if (Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(view) is not { } layer) return;
        var settings = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(layer).OfType<SlackBotSettingsView>().FirstOrDefault();
        if (settings is not null && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<ScrollViewer>(settings) is { } scroller) scroller.ScrollToEnd();
    }

    private static readonly IReadOnlyList<SlackConversation> Conversations =
    [
        new("C01REL", "release-bubble-pop", SlackConversationKind.Channel),
        new("C02BUG", "bug-bubble-pop", SlackConversationKind.Channel),
        new("C03GEN", "general", SlackConversationKind.Channel),
        new("C04ART", "art-ui", SlackConversationKind.PrivateChannel),
        new("C05RND", "random", SlackConversationKind.Channel),
        new("C06LIVE", "liveops-dashboard", SlackConversationKind.Channel),
        new("D01TUAN", "Tuấn (QA)", SlackConversationKind.DirectMessage, "U01TUAN"),
        new("D02MAI", "Mai (Art)", SlackConversationKind.DirectMessage, "U02MAI"),
        new("D03PO", "Trang (PO)", SlackConversationKind.DirectMessage, "U03PO"),
        new("G01", "Linh, Quân, Hùng", SlackConversationKind.GroupMessage),
    ];

    private static string Ago(double minutes) => SlackIds.Ts(DateTimeOffset.UtcNow.AddMinutes(-minutes));

    private static void Seed(SlackInbox inbox, bool unsorted)
    {
        inbox.WorkspaceUrl = "https://bubble-studio.slack.com/";
        InboxLevel Level(InboxLevel level) => unsorted ? InboxLevel.Pending : level;
        string? Line(string summary) => unsorted ? null : summary;

        // A DM with two messages: one card, waiting over two hours.
        var tuan = Ago(150);
        inbox.Add(new InboxItem
        {
            Channel = "D01TUAN", Ts = tuan, Kind = SlackConversationKind.DirectMessage, Where = "DM", From = "Tuấn (QA)", Direct = true,
            Text = "Anh ơi build Android 2.3.1 bị crash khi mở Shop trên Samsung A12, anh xem giúp em trước 3h chiều được không ạ? QA đang chặn test regression.",
            Level = Level(InboxLevel.Act), Summary = Line("Tuấn nhờ xem crash mở Shop (Android 2.3.1, Samsung A12) trước 15:00"),
        });
        inbox.Add(new InboxItem
        {
            Channel = "D01TUAN", Ts = Ago(140), Kind = SlackConversationKind.DirectMessage, Where = "DM", From = "Tuấn (QA)", Direct = true,
            Text = "Log với video em để trong Drive folder QA/2.3.1 rồi ạ.",
            Level = Level(InboxLevel.Act), Summary = Line("Tuấn đã để log và video trong Drive QA/2.3.1"),
        });

        // A thread in the release channel that calls the user, not sorted yet.
        var thread = Ago(60);
        inbox.Add(new InboxItem
        {
            Channel = "C01REL", Ts = Ago(35), ThreadTs = thread, Kind = SlackConversationKind.Channel, Where = "thread in #release-bubble-pop",
            From = "Linh", Direct = true, Tagged = true,
            Text = "@hung.nh chiều nay mình chốt version 2.3.1 lên store được chưa anh? Bên marketing cần ngày để lên lịch push.",
        });

        // The PO, in English.
        inbox.Add(new InboxItem
        {
            Channel = "D03PO", Ts = Ago(12), Kind = SlackConversationKind.DirectMessage, Where = "DM", From = "Trang (PO)", Direct = true,
            Text = "Hi Hùng, can you give me a rough estimate for the new daily reward popup before the sprint planning tomorrow at 10?",
            Level = Level(InboxLevel.Act), Summary = Line("Trang cần ước lượng popup daily reward trước sprint planning 10:00 mai"),
        });

        // Worth knowing, in three channels.
        inbox.Add(new InboxItem
        {
            Channel = "C01REL", Ts = Ago(95), Kind = SlackConversationKind.Channel, Where = "#release-bubble-pop", From = "Release Bot",
            Text = "Build #482 (iOS 2.3.1) uploaded to TestFlight. 0 warnings.", Level = Level(InboxLevel.Know),
            Summary = Line("Build iOS 2.3.1 #482 đã lên TestFlight"),
        });
        inbox.Add(new InboxItem
        {
            Channel = "C01REL", Ts = Ago(80), Kind = SlackConversationKind.Channel, Where = "#release-bubble-pop", From = "Quân",
            Text = "Remote config cho event Halloween đã bật 10% user, mọi người theo dõi crash rate giúp mình nhé.", Level = Level(InboxLevel.Know),
            Summary = Line("Event Halloween đã bật cho 10% người chơi, theo dõi crash rate"),
        });
        inbox.Add(new InboxItem
        {
            Channel = "C02BUG", Ts = Ago(70), Kind = SlackConversationKind.Channel, Where = "#bug-bubble-pop", From = "Tuấn (QA)",
            Text = "Bug #1287 (booster không trừ sau khi thua) đã verify fixed trên build 481.", Level = Level(InboxLevel.Know),
            Summary = Line("Bug #1287 booster đã được xác nhận sửa xong"),
        });
        inbox.Add(new InboxItem
        {
            Channel = "C03GEN", Ts = Ago(200), Kind = SlackConversationKind.Channel, Where = "#general", From = "HR Team",
            Text = "Thứ 6 này công ty tổ chức team building ở Ba Vì, mọi người đăng ký trước thứ 4 nha!", Level = Level(InboxLevel.Know),
            Summary = Line("Team building Ba Vì thứ 6, đăng ký trước thứ 4"),
        });
        inbox.Add(new InboxItem
        {
            Channel = "C03GEN", Ts = Ago(30), Kind = SlackConversationKind.Channel, Where = "#general", From = "IT Helpdesk",
            Text = "Tối nay 22:00 bảo trì VPN khoảng 30 phút.", Level = Level(InboxLevel.Know),
            Summary = Line("Bảo trì VPN tối nay 22:00, khoảng 30 phút"),
        });

        // Chatter Claude left out.
        inbox.Add(new InboxItem
        {
            Channel = "C05RND", Ts = Ago(25), Kind = SlackConversationKind.Channel, Where = "#random", From = "Nam",
            Text = "Ai order trà sữa không mọi người?", Level = Level(InboxLevel.Skip),
        });

        // Handled today: one answered by a reply in Slack, one marked done here.
        var mai = Ago(300);
        inbox.Add(new InboxItem
        {
            Channel = "D02MAI", Ts = mai, Kind = SlackConversationKind.DirectMessage, Where = "DM", From = "Mai (Art)", Direct = true,
            Text = "Anh ơi gửi em file spine của nhân vật mới nhé, em cần chỉnh animation idle.", Level = Level(InboxLevel.Act),
            Summary = Line("Mai xin file spine nhân vật mới để chỉnh animation idle"),
        });
        inbox.Mine(new SlackMessage("D02MAI", Ago(280), null, "U0HUNG", "Gửi em rồi nhé", false), SlackConversationKind.DirectMessage);
        var group = Ago(240);
        inbox.Add(new InboxItem
        {
            Channel = "G01", Ts = group, Kind = SlackConversationKind.GroupMessage, Where = "Linh, Quân", From = "Quân", Direct = true,
            Text = "Mai họp retro lúc 4h chiều nhé, mọi người chuẩn bị 2 điểm tốt 2 điểm chưa tốt.", Level = Level(InboxLevel.Act),
            Summary = Line("Họp retro 16:00 mai, chuẩn bị 2 điểm tốt và 2 điểm chưa tốt"),
        });
        inbox.Dismiss(SlackIds.Message("G01", group));
    }

    /// <summary>Keeps what it is given, but can never read it back: the saved tokens are never used, so nothing reaches Slack.</summary>
    private sealed class UnreadableProtector : ISecretProtector
    {
        private readonly PlainSecretProtector _inner = new();

        public byte[] Protect(byte[] data, string purpose) => _inner.Protect(data, purpose);

        public byte[] Unprotect(byte[] data, string purpose) =>
            purpose.Contains("tokens", StringComparison.Ordinal) ? throw new CryptographicException("Not in the Gallery.") : _inner.Unprotect(data, purpose);
    }

    /// <summary>No Claude in the Gallery: Sort now says so (or, for the warning, it is not installed).</summary>
    private sealed class GalleryAgent(bool missing) : ISlackAgent
    {
        public string? Unavailable() => missing ? "Claude Code is not installed on this PC." : null;

        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct) =>
            throw new ClaudeCodeException("Claude Code does not run in the Gallery.");
    }

    private sealed class NoLauncher : IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }

    private sealed class NoShell : ISlackBotShell
    {
        public void ShowPage() { }
        public void ResetOverlay() { }
    }

    private sealed class Host : ISlackBotPageHost
    {
        public void Opened() { }

        public void CopyText(string text) { }
    }
}
