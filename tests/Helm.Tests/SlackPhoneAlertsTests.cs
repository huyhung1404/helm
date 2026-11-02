using System.Net;
using System.Net.Http;
using System.Text;
using Helm.Core.Desktop;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.SlackBot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Alerts on the phone: only who, where and Claude's line, only while away, through the user's own bot.</summary>
public sealed class SlackPhoneAlertsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    internal sealed class FakeTelegram : ITelegram
    {
        public List<(long Chat, string Html)> Sent { get; } = [];

        public Task SendAsync(string token, long chat, string html, CancellationToken ct)
        {
            lock (Sent) Sent.Add((chat, html));
            return Task.CompletedTask;
        }

        public Task<(long Id, string Name)?> FindChatAsync(string token, CancellationToken ct) => Task.FromResult<(long, string)?>((42, "Hùng"));
    }

    /// <summary>The last input this many milliseconds ago.</summary>
    internal sealed class Activity(long idleMs) : IUserActivity
    {
        public long IdleMs { get; set; } = idleMs;

        public long LastInputTick => Environment.TickCount64 - IdleMs;
    }

    [Fact]
    public void Alerts_say_who_where_and_claudes_line_safely_but_never_the_message()
    {
        var line = new SlackAlertLine("Tuấn <dev>", "DM", "Nhờ xem lỗi build 2.3 & shop", "https://t.slack.com/archives/D1/p1?a=1&b=2", "Waiting 2 h");
        var reply = SlackPhoneAlerts.ReplyText(line);
        Assert.Contains("<b>Tuấn &lt;dev&gt;</b> · DM · <i>Waiting 2 h</i>", reply);
        Assert.Contains("Nhờ xem lỗi build 2.3 &amp; shop", reply);
        Assert.Contains("href=\"https://t.slack.com/archives/D1/p1?a=1&amp;b=2\"", reply);
        var remind = SlackPhoneAlerts.RemindText(Enumerable.Repeat(line with { Summary = null }, 7).ToList());
        Assert.StartsWith("⏰ <b>7 tin vẫn chờ anh</b>", remind);
        Assert.Contains("(chưa tóm tắt)", remind);
        Assert.Contains("và 2 tin nữa", remind);
    }

    [Fact]
    public async Task Replies_reach_the_phone_only_while_away_and_once_set_up()
    {
        var paths = new HelmPaths(_dir.Path);
        using var settings = new SettingsStoreFactory(paths);
        var protector = new PlainSecretProtector();
        using var file = new SlackInboxFile(paths, protector, NullLogger<SlackInboxFile>.Instance);
        var agent = new NoAgent();
        var engine = new SlackBotEngine(new SlackTokens(paths, protector), file, new SlackSorter(agent), agent, settings, NullLogger<SlackBotEngine>.Instance);
        var inbox = new SlackBotInboxViewModel(engine, new InlineUi(), new NoLauncher(), new NoServices(), settings);
        var telegram = new FakeTelegram();
        var token = new SlackTelegramToken(paths, protector);
        var activity = new Activity(idleMs: 0);
        var phone = new SlackPhoneAlerts(telegram, token, settings, activity, engine, inbox, NullLogger<SlackPhoneAlerts>.Instance);
        phone.Start();

        // Not set up: nothing goes.
        engine.RaiseRepliedForTests(new SlackAlertLine("Tuấn", "DM", "xem build", "https://x"));
        token.Save("123456789:AAH" + new string('x', 30));
        settings.Get<SlackBotSettings>(SlackBotModule.ModuleId).Update(s => s.TelegramChatId = 42);
        // At the PC: nothing goes.
        engine.RaiseRepliedForTests(new SlackAlertLine("Tuấn", "DM", "xem build", "https://x"));
        // Away for 10 minutes: it goes.
        activity.IdleMs = 10 * 60_000;
        engine.RaiseRepliedForTests(new SlackAlertLine("Tuấn", "DM", "xem build", "https://x"));
        for (var i = 0; i < 50 && telegram.Sent.Count == 0; i++) await Task.Delay(20);
        var sent = Assert.Single(telegram.Sent);
        Assert.Equal(42, sent.Chat);
        Assert.StartsWith("🤖 <b>Lumi đã trả lời hộ anh</b>", sent.Html);

        Assert.Null(await phone.TestAsync(default));
        Assert.Equal(2, telegram.Sent.Count);
    }

    [Fact]
    public async Task The_chat_is_the_last_private_one_that_wrote_to_the_bot()
    {
        var handler = new Handler("""{"ok":true,"result":[{"message":{"chat":{"id":7,"type":"group"}}},{"message":{"chat":{"id":42,"type":"private","first_name":"Hùng","last_name":"Nguyễn"}}}]}""");
        using var api = new TelegramApi(handler);
        Assert.Equal((42L, "Hùng Nguyễn"), await api.FindChatAsync("1:x", default));
        Assert.Contains("/bot1:x/getUpdates", handler.Last);

        using var refused = new TelegramApi(new Handler("""{"ok":false,"description":"Unauthorized"}""", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<TelegramException>(() => refused.SendAsync("1:x", 42, "hi", default));
        Assert.Contains("Unauthorized", ex.Message);
        Assert.Null(SlackTelegramToken.Problem("123456789:AAH" + new string('x', 30)));
        Assert.NotNull(SlackTelegramToken.Problem("xoxb-1"));
    }

    private sealed class Handler(string json, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string Last { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoAgent : ISlackAgent
    {
        public string? Unavailable() => "no";
        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoLauncher : Helm.Core.Services.IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }
}
