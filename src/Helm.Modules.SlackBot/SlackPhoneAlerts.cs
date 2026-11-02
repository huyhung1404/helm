using System.Net;
using System.Text;
using Helm.Core.Desktop;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.SlackBot;

/// <summary>One line of an alert: who wrote, where, Claude's one-line summary (never the message itself), and the link.</summary>
public sealed record SlackAlertLine(string From, string Where, string? Summary, string Link, string Waiting = "");

/// <summary>
/// Tells the user on their phone, through their own Telegram bot, what Slack Bot did while they were away from the PC:
/// a reply Lumi sent for them, and what still waits when the list reminds. Only who, where and Claude's line go out
/// (Telegram is outside the workspace), never the message itself. Away = no keyboard or mouse for a few minutes; the
/// user may also have it sent while they sit at the PC. Sending never throws.
/// </summary>
public sealed class SlackPhoneAlerts
{
    private readonly ITelegram _telegram;
    private readonly SlackTelegramToken _token;
    private readonly ISettingsStore<SlackBotSettings> _settings;
    private readonly IUserActivity _activity;
    private readonly ILogger<SlackPhoneAlerts> _logger;
    private bool _running;

    public SlackPhoneAlerts(ITelegram telegram, SlackTelegramToken token, ISettingsStoreFactory settings, IUserActivity activity,
        SlackBotEngine engine, SlackBotInboxViewModel inbox, ILogger<SlackPhoneAlerts> logger)
    {
        _telegram = telegram;
        _token = token;
        _settings = settings.Get<SlackBotSettings>(SlackBotModule.ModuleId);
        _activity = activity;
        _logger = logger;
        engine.Replied += (_, line) =>
        {
            if (_settings.Current.TelegramOnReply) Send(ReplyText(line));
        };
        inbox.Remind += (_, _) =>
        {
            if (_settings.Current.TelegramOnRemind) Send(RemindText(inbox.WaitingLines()));
        };
        inbox.NewToDoLines += (_, lines) =>
        {
            if (_settings.Current.TelegramOnNewToDo) Send(NewText(lines));
        };
    }

    public void Start() => _running = true;

    public void Stop() => _running = false;

    /// <summary>Nobody touched the keyboard or mouse for the minutes the user set.</summary>
    internal bool IsAway =>
        TimeSpan.FromMilliseconds(Environment.TickCount64 - _activity.LastInputTick) >= TimeSpan.FromMinutes(Math.Max(1, _settings.Current.AwayMinutes));

    /// <summary>Sends a test message now; null when it went, else why not.</summary>
    public async Task<string?> TestAsync(CancellationToken ct)
    {
        if (_token.Load() is not { } token) return "Save the bot token first.";
        if (_settings.Current.TelegramChatId is not { } chat) return "Find your chat first (send /start to your bot).";
        try
        {
            await _telegram.SendAsync(token, chat, "🤖 <b>Lumi</b> sẽ báo anh ở đây khi anh không ngồi máy.", ct).ConfigureAwait(false);
            return null;
        }
        catch (TelegramException ex)
        {
            return ex.Message;
        }
    }

    internal static string ReplyText(SlackAlertLine line) =>
        $"🤖 <b>Lumi đã trả lời hộ anh</b>\n{Line(line)}";

    internal static string RemindText(IReadOnlyList<SlackAlertLine> lines)
    {
        var text = new StringBuilder($"⏰ <b>{lines.Count} tin vẫn chờ anh</b>");
        foreach (var line in lines.Take(5)) text.Append("\n\n").Append(Line(line));
        if (lines.Count > 5) text.Append($"\n\n… và {lines.Count - 5} tin nữa.");
        return text.ToString();
    }

    internal static string NewText(IReadOnlyList<SlackAlertLine> lines)
    {
        var text = new StringBuilder(lines.Count == 1 ? "🔴 <b>Tin mới cần anh</b>" : $"🔴 <b>{lines.Count} tin mới cần anh</b>");
        foreach (var line in lines.Take(5)) text.Append("\n\n").Append(Line(line));
        return text.ToString();
    }

    private static string Line(SlackAlertLine line)
    {
        var text = new StringBuilder($"<b>{Html(line.From)}</b> · {Html(line.Where)}");
        if (line.Waiting.Length > 0) text.Append($" · <i>{Html(line.Waiting)}</i>");
        text.Append('\n').Append(line.Summary is { Length: > 0 } s ? Html(s) : "<i>(chưa tóm tắt)</i>");
        text.Append($"\n<a href=\"{Html(line.Link)}\">Mở trên Slack</a>");
        return text.ToString();
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private void Send(string html)
    {
        if (!_running || !_settings.Current.TelegramEnabled) return;
        if (_settings.Current.TelegramOnlyAway && !IsAway) return;
        if (_settings.Current.TelegramChatId is not { } chat || _token.Load() is not { } token) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _telegram.SendAsync(token, chat, html, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Slack Bot could not send to Telegram");
            }
        });
    }
}
