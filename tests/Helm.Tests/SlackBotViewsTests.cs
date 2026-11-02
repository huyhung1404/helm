using System.Windows;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.SlackBot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The SlackBot overlay and pages load with messages, built off-screen: no window is shown (the overlay is topmost).
/// With HELM_SLACKBOT_SNAPSHOTS set to a folder each one is rendered to a PNG there, for checking the layout by eye.
/// </summary>
public sealed class SlackBotViewsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class NoAgent : ISlackAgent
    {
        public string? Unavailable() => "Claude Code is not installed on this PC.";

        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Launcher : IProcessLauncher
    {
        public List<string> Opened { get; } = [];
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) => Opened.Add(url);
        public void StartNewInstance(string? arguments = null) { }
    }

    /// <summary>The STA thread's dispatcher, like the app's UI thread: the view model's delayed refresh comes back to it.</summary>
    private sealed class StaUi : IUiDispatcher
    {
        private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        public bool CheckAccess() => _dispatcher.CheckAccess();
        public void Post(Action action) => _dispatcher.BeginInvoke(action);
        public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
    }

    private sealed class NoSlack : ISlackApi
    {
        public Task<SlackIdentity> AuthTestAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackConversation>> ConversationsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackMessage>> HistoryAsync(string channel, string oldest, int max, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackMessage>> RepliesAsync(string channel, string threadTs, string oldest, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SlackMessage>> SearchMentionsAsync(string userId, string oldest, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> UserNameAsync(string userId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<Uri> OpenSocketAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> PostMessageAsync(string channel, string text, string? blocks, string? threadTs, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void The_overlay_and_pages_load_with_messages() => WpfHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var paths = new HelmPaths(_dir.Path);
        using var settings = new SettingsStoreFactory(paths);
        var protector = new PlainSecretProtector();
        var tokens = new SlackTokens(paths, protector);
        using var file = new SlackInboxFile(paths, protector, NullLogger<SlackInboxFile>.Instance);
        Seed(file.Inbox);
        var engine = new SlackBotEngine(tokens, file, new SlackSorter(new NoAgent()), new NoAgent(), settings, NullLogger<SlackBotEngine>.Instance);
        var launcher = new Launcher();
        var ui = new StaUi();
        var inbox = new SlackBotInboxViewModel(engine, ui, launcher, new NoServices(), settings);
        var windows = new WindowService(NullLogger<WindowService>.Instance);
        var overlay = new SlackBotOverlay(inbox, settings, windows, ui, NullLogger<SlackBotOverlay>.Instance);
        var telegramToken = new SlackTelegramToken(paths, protector);
        var telegram = new SlackPhoneAlertsTests.FakeTelegram();
        var phone = new SlackPhoneAlerts(telegram, telegramToken, settings, new SlackPhoneAlertsTests.Activity(0), engine, inbox,
            NullLogger<SlackPhoneAlerts>.Instance);
        var module = new SlackBotModule(engine, overlay, phone) { IsEnabled = true };

        // To do: the DM and the mention (one unsorted), not the channel chatter or what the user answered.
        Assert.Equal(["Tuấn", "Linh"], inbox.ToDo.Select(i => i.From));
        Assert.True(inbox.ToDo[1].IsUnsorted);
        Assert.Equal(2, inbox.ToDoCount);
        Assert.Single(inbox.Handled);
        // The report shows what came before the last report time.
        Assert.Equal(1, inbox.ReportCount);
        Assert.True(inbox.HasOverlay);

        Layout(new System.Windows.Controls.Image { Source = SlackBotLogo.Image, Width = 128, Height = 128 }, new Size(128, 128), "0-icon");
        var open = new SlackBotOverlayWindow(inbox, windows, null, false, _ => { }, _ => { });
        Layout(open, new Size(400, 520), "1-overlay");
        var folded = new SlackBotOverlayWindow(inbox, windows, null, true, _ => { }, _ => { });
        Layout(folded, new Size(200, 80), "2-overlay-folded");

        inbox.OpenCommand.Execute(inbox.ToDo[0]);
        Assert.Equal("https://team.slack.com/archives/D1/p1700000000000100", Assert.Single(launcher.Opened));

        var content = new SlackBotContentPage(module, inbox);
        Layout(content, new Size(1000, 1300), "3-content");

        engine.ConnectedForTests(new NoSlack(), new SlackIdentity("ME", "hung", "T1", "https://team.slack.com/"), "Hưng",
        [
            new SlackConversation("C1", "release", SlackConversationKind.Channel),
            new SlackConversation("C2", "fast-core-game", SlackConversationKind.PrivateChannel),
            new SlackConversation("D1", "Tuấn", SlackConversationKind.DirectMessage, "U2"),
        ]);
        settings.Get<SlackBotSettings>(SlackBotModule.ModuleId).Update(v => v.Channels["C1"] = SlackChannelMode.Reply);
        var viewModel = new SlackBotViewModel(module, engine, tokens, settings, ui, launcher, new NoServices(), overlay, telegramToken, telegram, phone);
        Assert.False(viewModel.HasTokens);
        Assert.Equal([true, false, true], viewModel.Channels.Select(c => c.CanRead));
        Assert.Equal([true, false, false], viewModel.Channels.Select(c => c.CanWrite));
        Layout(new SlackBotPage(viewModel), new Size(1000, 1500), "4-settings");

        // Done takes it off the list; replying in the thread takes the mention off too.
        inbox.DoneCommand.Execute(inbox.ToDo[0]);
        engine.Inbox.Mine(new SlackMessage("C1", "1700000300.000100", "1700000200.000100", "ME", "đang xem", false), SlackConversationKind.Channel);
        Flush();
        Assert.Empty(inbox.ToDo);
        Assert.Equal(3, inbox.Handled.Count);
    });

    private static void Seed(SlackInbox inbox)
    {
        inbox.WorkspaceUrl = "https://team.slack.com/";
        inbox.Add(new InboxItem
        {
            Channel = "D1", Ts = "1700000000.000100", Kind = SlackConversationKind.DirectMessage, Where = "DM", From = "Tuấn", Direct = true,
            Text = "Anh ơi build Android bản 2.3 lỗi khi mở shop, anh xem giúp em trước 3h chiều được không ạ?",
            Level = InboxLevel.Act, Summary = "Tuấn nhờ anh xem lỗi build Android 2.3 (mở shop) trước 15:00",
        });
        inbox.Add(new InboxItem
        {
            Channel = "C1", Ts = "1700000250.000100", ThreadTs = "1700000200.000100", Kind = SlackConversationKind.Channel,
            Where = "thread in #release", From = "Linh", Direct = true,
            Text = "@hung chiều nay chốt version chưa anh? QA đang chờ để test regression.",
        });
        inbox.Add(new InboxItem
        {
            Channel = "C2", Ts = "1600000000.000100", Kind = SlackConversationKind.Channel, Where = "#general", From = "HR",
            Text = "Thứ 6 này công ty tổ chức team building, mọi người đăng ký trước thứ 4 nha.", Level = InboxLevel.Know,
            Summary = "Team building thứ 6, đăng ký trước thứ 4",
        });
        inbox.Add(new InboxItem
        {
            Channel = "C2", Ts = "1700000100.000100", Kind = SlackConversationKind.Channel, Where = "#general", From = "Nam",
            Text = "Ai ăn trưa không?", Level = InboxLevel.Skip,
        });
        inbox.Add(new InboxItem
        {
            Channel = "D2", Ts = "1700000050.000100", Kind = SlackConversationKind.DirectMessage, Where = "DM", From = "Mai", Direct = true,
            Text = "Anh gửi em file design nhé", Level = InboxLevel.Act, Summary = "Mai xin file design",
        });
        inbox.Reacted("D2", "1700000050.000100");
        inbox.BotReplied("D1:1700000000.000100", "D1", "1700000001.000100", "Dạ em đã nhận được và báo anh ấy rồi ạ, anh ấy sẽ xem trước 15:00.");
        inbox.Digest(DateTimeOffset.FromUnixTimeSeconds(1_650_000_000));
    }

    /// <summary>Lets the view model's short refresh delay run.</summary>
    private static void Flush()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Layout(FrameworkElement element, Size size, string name)
    {
        // A window that is never shown does not lay out; its content does.
        var target = element is Window { Content: FrameworkElement content } ? content : element;
        target.Measure(size);
        target.Arrange(new Rect(size));
        target.UpdateLayout();
        var idle = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => idle.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(idle);
        target.UpdateLayout();
        if (Environment.GetEnvironmentVariable("HELM_SLACKBOT_SNAPSHOTS") is not { Length: > 0 } folder) return;
        var host = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("ApplicationBackgroundBrush"),
            Width = size.Width,
            Height = size.Height,
        };
        host.Measure(size);
        host.Arrange(new Rect(size));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Render(target);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var png = File.Create(Path.Combine(folder, $"{name}.png"));
        encoder.Save(png);
    }
}
