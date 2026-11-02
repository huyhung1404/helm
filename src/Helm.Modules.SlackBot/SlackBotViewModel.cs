using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

public sealed record SlackBotChoice<T>(T Value, string Label);

/// <summary>A channel or DM in the "Channels SlackBot reads" list.</summary>
public sealed partial class SlackChannelRow : ObservableObject
{
    private readonly Action<string, bool> _changed;

    [ObservableProperty] private bool _isRead;

    public SlackChannelRow(SlackConversation conversation, bool read, Action<string, bool> changed)
    {
        Id = conversation.Id;
        Name = conversation.Kind switch
        {
            SlackConversationKind.DirectMessage => "DM: " + conversation.Name,
            SlackConversationKind.GroupMessage => "Group DM: " + conversation.Name,
            SlackConversationKind.PrivateChannel => "🔒 " + conversation.Name,
            _ => "#" + conversation.Name,
        };
        _isRead = read;
        _changed = changed;
    }

    public string Id { get; }

    public string Name { get; }

    partial void OnIsReadChanged(bool value) => _changed(Id, value);
}

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
    private List<SlackChannelRow> _allChannels = [];
    private bool _loading;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasTokens;
    [ObservableProperty] private string? _tokenProblem;
    [ObservableProperty] private string _reportTimes = "";
    [ObservableProperty] private int _channelMinutes;
    [ObservableProperty] private bool _readBots;
    [ObservableProperty] private string _aboutMe = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private bool _playSound;
    [ObservableProperty] private string _channelFilter = "";
    [ObservableProperty] private string _channelsSummary = "";

    public SlackBotViewModel(SlackBotModule module, SlackBotEngine engine, SlackTokens tokens, ISettingsStoreFactory settings, IUiDispatcher ui,
        IProcessLauncher launcher, IServiceProvider services, SlackBotOverlay overlay)
    {
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

    public IReadOnlyList<SlackBotChoice<int>> ChannelMinuteChoices { get; } =
    [
        new(15, "Every 15 minutes"),
        new(30, "Every 30 minutes"),
        new(60, "Every hour"),
        new(120, "Every 2 hours"),
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

    partial void OnChannelMinutesChanged(int value) => Save(s => s.ChannelMinutes = value);

    partial void OnReadBotsChanged(bool value) => Save(s => s.ReadBots = value);

    partial void OnAboutMeChanged(string value) => Save(s => s.AboutMe = value);

    partial void OnModelChanged(string value) => Save(s => s.Model = value);

    partial void OnPlaySoundChanged(bool value) => Save(s => s.PlaySound = value);

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
            ChannelMinutes = s.ChannelMinutes;
            ReadBots = s.ReadBots;
            AboutMe = s.AboutMe;
            Model = s.Model;
            PlaySound = s.PlaySound;
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
        Status = _engine.Status is { Length: > 0 } s ? s : _engine.State == SlackBotState.Off ? "SlackBot is turned off." : "";
        var conversations = _engine.Conversations;
        if (conversations.Count == _allChannels.Count && conversations.Select(c => c.Id).SequenceEqual(_allChannels.Select(r => r.Id)))
        {
            UpdateSummary();
            return;
        }
        var blocked = _store.Current.Blocked.ToHashSet();
        _allChannels = conversations.Select(c => new SlackChannelRow(c, !blocked.Contains(c.Id), SetRead)).ToList();
        FilterChannels();
    }

    private void SetRead(string id, bool read)
    {
        _store.Update(s =>
        {
            s.Blocked.Remove(id);
            if (!read) s.Blocked.Add(id);
        });
        if (!read) _engine.Inbox.Forget(id);
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
        var blocked = _allChannels.Count(r => !r.IsRead);
        ChannelsSummary = _allChannels.Count == 0
            ? "The list appears once SlackBot is connected."
            : blocked == 0 ? $"All {_allChannels.Count} channels and DMs are read." : $"{blocked} of {_allChannels.Count} are not read.";
    }
}
