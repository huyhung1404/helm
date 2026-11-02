using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

public sealed record SlackBotChoice<T>(T Value, string Label);

/// <summary>The settings page: the connection, the channels it reads, how it sorts, and when it reports.</summary>
public sealed partial class SlackBotViewModel : ObservableObject
{
    private readonly ISettingsStore<SlackBotSettings> _store;
    private readonly SlackBotEngine _engine;
    private readonly SlackTokens _tokens;
    private readonly IUiDispatcher _ui;
    private readonly IProcessLauncher _launcher;
    private readonly IServiceProvider _services;
    private readonly SlackBotOverlay _overlay;
    private readonly SlackTelegramToken _telegramToken;
    private readonly ITelegram _telegram;
    private readonly SlackPhoneAlerts _phone;
    private List<SlackChannelRow> _allChannels = [];
    private bool _loading;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasTokens;
    [ObservableProperty] private string? _tokenProblem;
    [ObservableProperty] private string _reportTimes = "";
    [ObservableProperty] private int _channelSeconds;
    [ObservableProperty] private bool _readBots;
    [ObservableProperty] private bool _catchUpQuietDms;
    [ObservableProperty] private string _aboutMe = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private bool _playSound;
    [ObservableProperty] private int _remindEveryMinutes;
    [ObservableProperty] private bool _answerCalls;
    [ObservableProperty] private bool _telegramEnabled;
    [ObservableProperty] private bool _telegramOnReply;
    [ObservableProperty] private bool _telegramOnRemind;
    [ObservableProperty] private bool _telegramOnNewToDo;
    [ObservableProperty] private bool _telegramOnlyAway;
    [ObservableProperty] private int _awayMinutes;
    [ObservableProperty] private string _telegramStatus = "";
    [ObservableProperty] private bool _hasTelegramToken;
    [ObservableProperty] private string _channelFilter = "";
    [ObservableProperty] private string _channelsSummary = "";
    [ObservableProperty] private string _signature = "";
    [ObservableProperty] private string _signatureEmoji = "";
    [ObservableProperty] private string _replyStyle = "";
    [ObservableProperty] private string _replyPreview = "";
    [ObservableProperty] private int _replyAfterMinutes;
    [ObservableProperty] private double _overlayOpacity;

    public SlackBotViewModel(SlackBotModule module, SlackBotEngine engine, SlackTokens tokens, ISettingsStoreFactory settings, IUiDispatcher ui,
        IProcessLauncher launcher, IServiceProvider services, SlackBotOverlay overlay, SlackTelegramToken telegramToken, ITelegram telegram,
        SlackPhoneAlerts phone)
    {
        _telegramToken = telegramToken;
        _telegram = telegram;
        _phone = phone;
        _overlay = overlay;
        Module = module;
        _engine = engine;
        _tokens = tokens;
        _store = settings.Get<SlackBotSettings>(SlackBotModule.ModuleId);
        _ui = ui;
        _launcher = launcher;
        _services = services;
        Load(_store.Current);
        engine.Changed += (_, _) => _ui.Post(RefreshEngine);
        RefreshEngine();
    }

    public SlackBotModule Module { get; }

    public string Manifest => SlackAppManifest.Json;

    public ObservableCollection<SlackChannelRow> Channels { get; } = [];

    public IReadOnlyList<SlackBotChoice<int>> ReplyAfterChoices { get; } =
    [
        new(1, "After 1 minute"),
        new(3, "After 3 minutes"),
        new(5, "After 5 minutes"),
        new(10, "After 10 minutes"),
        new(30, "After 30 minutes"),
    ];

    public IReadOnlyList<SlackBotChoice<int>> RemindChoices { get; } =
    [
        new(0, "Never"),
        new(30, "Every 30 minutes"),
        new(60, "Every hour"),
        new(120, "Every 2 hours"),
        new(240, "Every 4 hours"),
    ];

    public IReadOnlyList<SlackBotChoice<string>> Models { get; } =
    [
        new("", "Claude Code's default"),
        new("haiku", "Haiku (fast, light on your plan)"),
        new("sonnet", "Sonnet"),
    ];

    /// <summary>Saves new tokens and connects with them; false (with <see cref="TokenProblem"/>) when they cannot be right.</summary>
    public async Task<bool> ConnectAsync(string userToken, string appToken)
    {
        var pair = new SlackTokenPair(userToken.Trim(), appToken.Trim());
        TokenProblem = pair.Problem();
        if (TokenProblem is not null) return false;
        _tokens.Save(pair);
        HasTokens = true;
        await _engine.RestartAsync();
        return true;
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        _tokens.Delete();
        HasTokens = false;
        await _engine.RestartAsync();
    }

    [RelayCommand]
    private Task Reconnect() => _engine.RestartAsync();

    [RelayCommand]
    private void OpenSlackApps() => _launcher.OpenUrl(SlackAppManifest.AppsUrl);

    [RelayCommand]
    private void OpenContent() => _services.GetRequiredService<IShellNavigation>().ShowPage(typeof(SlackBotContentPage));

    [RelayCommand]
    private void ResetOverlay() => _overlay.Reset();

    partial void OnReportTimesChanged(string value)
    {
        if (_loading) return;
        var times = ParseTimes(value);
        if (times is not null) _store.Update(s => s.ReportTimes = times);
    }

    partial void OnChannelSecondsChanged(int value)
    {
        if (value < SlackBotSettings.MinChannelSeconds)
        {
            ChannelSeconds = SlackBotSettings.MinChannelSeconds;
            return;
        }
        Save(s => s.ChannelSeconds = value);
    }

    partial void OnReadBotsChanged(bool value) => Save(s => s.ReadBots = value);

    partial void OnCatchUpQuietDmsChanged(bool value) => Save(s => s.CatchUpQuietDms = value);

    partial void OnAboutMeChanged(string value) => Save(s => s.AboutMe = value);

    partial void OnModelChanged(string value) => Save(s => s.Model = value);

    partial void OnPlaySoundChanged(bool value) => Save(s => s.PlaySound = value);

    partial void OnRemindEveryMinutesChanged(int value) => Save(s => s.RemindEveryMinutes = value);

    partial void OnAnswerCallsChanged(bool value) => Save(s => s.AnswerCalls = value);

    partial void OnTelegramEnabledChanged(bool value) => Save(s => s.TelegramEnabled = value);

    partial void OnTelegramOnReplyChanged(bool value) => Save(s => s.TelegramOnReply = value);

    partial void OnTelegramOnRemindChanged(bool value) => Save(s => s.TelegramOnRemind = value);

    partial void OnTelegramOnNewToDoChanged(bool value) => Save(s => s.TelegramOnNewToDo = value);

    partial void OnTelegramOnlyAwayChanged(bool value) => Save(s => s.TelegramOnlyAway = value);

    partial void OnAwayMinutesChanged(int value) => Save(s => s.AwayMinutes = Math.Clamp(value, 1, 120));

    public IReadOnlyList<SlackBotChoice<int>> AwayChoices { get; } =
    [
        new(2, "2 minutes"),
        new(5, "5 minutes"),
        new(10, "10 minutes"),
        new(30, "30 minutes"),
    ];

    /// <summary>The phone is set up (a chat was found).</summary>
    public bool TelegramConnected => _store.Current.TelegramChatId is not null;

    /// <summary>Saves the bot's token (from the password box); false with the reason in <see cref="TelegramStatus"/>.</summary>
    public bool SaveTelegramToken(string token)
    {
        if (SlackTelegramToken.Problem(token) is { } problem)
        {
            TelegramStatus = problem;
            return false;
        }
        _telegramToken.Save(token.Trim());
        HasTelegramToken = true;
        TelegramStatus = "Token saved. Now send /start to your bot in Telegram, then press Find my chat.";
        return true;
    }

    [RelayCommand]
    private async Task FindTelegramChat()
    {
        if (_telegramToken.Load() is not { } token)
        {
            TelegramStatus = "Save the bot token first.";
            return;
        }
        try
        {
            if (await _telegram.FindChatAsync(token, CancellationToken.None) is not { } chat)
            {
                TelegramStatus = "Nobody wrote to the bot yet: open it in Telegram, send /start, then press Find my chat again.";
                return;
            }
            _store.Update(s =>
            {
                s.TelegramChatId = chat.Id;
                s.TelegramChatName = chat.Name;
            });
            TelegramStatus = (await _phone.TestAsync(CancellationToken.None)) is { } why
                ? why
                : $"Connected to {chat.Name}: a test message is on your phone.";
        }
        catch (TelegramException ex)
        {
            TelegramStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestTelegram() =>
        TelegramStatus = await _phone.TestAsync(CancellationToken.None) ?? "Sent: look at your phone.";

    [RelayCommand]
    private void ForgetTelegram()
    {
        _telegramToken.Delete();
        _store.Update(s =>
        {
            s.TelegramChatId = null;
            s.TelegramChatName = "";
        });
        HasTelegramToken = false;
        TelegramStatus = "Telegram is disconnected.";
    }

    partial void OnSignatureChanged(string value)
    {
        UpdatePreview();
        Save(s => s.Signature = value);
    }

    partial void OnReplyStyleChanged(string value) => Save(s => s.ReplyStyle = value);

    partial void OnSignatureEmojiChanged(string value)
    {
        UpdatePreview();
        Save(s => s.SignatureEmoji = value);
    }

    partial void OnReplyAfterMinutesChanged(int value) => Save(s => s.ReplyAfterMinutes = value);

    partial void OnOverlayOpacityChanged(double value) => Save(s => s.OverlayOpacity = Math.Clamp(value, 0.3, 1));

    [RelayCommand]
    private void ResetSignature()
    {
        Signature = SlackBotSettings.DefaultSignature;
        SignatureEmoji = SlackSignature.DefaultEmoji;
    }

    partial void OnChannelFilterChanged(string value) => FilterChannels();

    /// <summary>"12:00, 17:30" → the times; null when one of them is not a time.</summary>
    internal static List<string>? ParseTimes(string text)
    {
        var list = new List<string>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TimeOnly.TryParseExact(part, ["H:mm", "HH:mm", "%H"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
            list.Add(t.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        return list.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    private void Load(SlackBotSettings s)
    {
        _loading = true;
        try
        {
            ReportTimes = string.Join(", ", s.ReportTimes);
            ChannelSeconds = s.ChannelSeconds;
            ReadBots = s.ReadBots;
            CatchUpQuietDms = s.CatchUpQuietDms;
            AboutMe = s.AboutMe;
            Model = s.Model;
            PlaySound = s.PlaySound;
            RemindEveryMinutes = s.RemindEveryMinutes;
            AnswerCalls = s.AnswerCalls;
            TelegramEnabled = s.TelegramEnabled;
            TelegramOnReply = s.TelegramOnReply;
            TelegramOnRemind = s.TelegramOnRemind;
            TelegramOnNewToDo = s.TelegramOnNewToDo;
            TelegramOnlyAway = s.TelegramOnlyAway;
            AwayMinutes = s.AwayMinutes;
            HasTelegramToken = _telegramToken.Exists;
            TelegramStatus = s.TelegramChatId is null
                ? (HasTelegramToken ? "Send /start to your bot in Telegram, then press Find my chat." : "Not set up.")
                : $"Connected to {s.TelegramChatName}.";
            Signature = s.Signature;
            SignatureEmoji = s.SignatureEmoji;
            ReplyStyle = s.ReplyStyle;
            ReplyAfterMinutes = s.ReplyAfterMinutes;
            OverlayOpacity = s.OverlayOpacity;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Action<SlackBotSettings> change)
    {
        if (!_loading) _store.Update(change);
    }

    private void RefreshEngine()
    {
        HasTokens = _tokens.Exist;
        Status = _engine.Status is { Length: > 0 } s ? s : _engine.State == SlackBotState.Off ? "Slack Bot is turned off." : "";
        // DMs with people who left are not worth a switch.
        var conversations = _engine.Conversations.Where(c => !c.Gone).ToList();
        if (conversations.Count == _allChannels.Count && conversations.Select(c => c.Id).SequenceEqual(_allChannels.Select(r => r.Id)))
        {
            UpdateSummary();
            return;
        }
        var settings = _store.Current;
        _allChannels = conversations.Select(c => new SlackChannelRow(c, settings.ModeOf(c.Id, c.Kind), SetMode,
            settings.ChannelProjects.GetValueOrDefault(c.Id), SetProject)).ToList();
        FilterChannels();
        UpdatePreview();
    }

    private void SetProject(string id, string? folder) => _store.Update(s =>
    {
        if (folder is null) s.ChannelProjects.Remove(id);
        else s.ChannelProjects[id] = folder;
    });

    private void SetMode(string id, SlackChannelMode mode)
    {
        _store.Update(s => s.Channels[id] = mode);
        // Turned off: nothing from it is kept any more.
        if (mode == SlackChannelMode.Off) _engine.Inbox.Forget(id);
        UpdateSummary();
    }

    private void FilterChannels()
    {
        var filter = ChannelFilter.Trim();
        Channels.Clear();
        foreach (var row in _allChannels)
            if (filter.Length == 0 || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) Channels.Add(row);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var read = _allChannels.Count(r => r.Mode != SlackChannelMode.Off);
        var reply = _allChannels.Count(r => r.Mode == SlackChannelMode.Reply);
        ChannelsSummary = _allChannels.Count == 0
            ? "The list appears once Slack Bot is connected."
            : $"Reads {read} of {_allChannels.Count}, replies in {reply}. DMs are read unless you turn them off; channels are off until you turn them on.";
    }

    /// <summary>How a reply looks: an example of what Claude writes, then the signature line.</summary>
    private void UpdatePreview() =>
        ReplyPreview = new SlackSignedMessage("Dạ em đã nhận được tin nhắn và báo lại rồi ạ, anh ấy sẽ phản hồi sớm nhất có thể.",
            SlackSignature.Label(Signature, _engine.MeName is { Length: > 0 } me ? me : "you"), SlackSignature.Emoji(SignatureEmoji)).Preview;
}
