using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Home;

/// <summary>One tool of Helm: its name and own icon, which part of life it belongs to, and whether it is on.</summary>
public sealed partial class HomeTool(string id, string name, string group, Symbol icon, IImage? image = null, bool isEnabled = true) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>"Planning", "Money &amp; Media"…: the caption in the list of all tools.</summary>
    public string Group { get; } = group;

    /// <summary>Drawn only when the tool has no picture of its own.</summary>
    public Symbol Icon { get; } = icon;

    /// <summary>The tool's own icon (the same picture as in the menu).</summary>
    public IImage? Image { get; } = image;

    /// <summary>On or off. The platform turns the tool on or off when this changes.</summary>
    [ObservableProperty] private bool _isEnabled = isEnabled;
}

/// <summary>How bad a problem is: something broke, or something wants a decision.</summary>
public enum AlertLevel
{
    Danger,
    Attention,
}

/// <summary>Something about Helm itself that needs the user: an update, a shortcut taken, a vault never backed up, sync stuck.</summary>
public sealed partial class HomeAlert(string title, string detail, AlertLevel level, string actionText, Func<Task> act) : ObservableObject
{
    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public AlertLevel Level { get; } = level;

    public bool IsDanger => Level == AlertLevel.Danger;

    public string ActionText { get; } = actionText;

    [RelayCommand]
    private Task ActAsync() => act();
}

/// <summary>One thing for today: a task, a habit, a bill. Some can be done right on Home (a tick).</summary>
public sealed partial class TodayItem(string text, string? detail = null, bool isLate = false, Action<TodayItem>? tick = null) : ObservableObject
{
    public string Text { get; } = text;

    public string? Detail { get; } = detail;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>Overdue: shown in red.</summary>
    public bool IsLate { get; } = isLate;

    public bool CanTick => tick is not null;

    [ObservableProperty] private bool _isDone;

    [RelayCommand]
    private void Tick()
    {
        if (tick is null) return;
        IsDone = !IsDone;
        tick(this);
    }
}

/// <summary>What one tool has for today, with what to do about it.</summary>
public sealed partial class TodayCard(HomeTool tool, string headline, IEnumerable<TodayItem> items, string? actionText, Func<Task>? act, int urgency) : ObservableObject
{
    public HomeTool Tool { get; } = tool;

    /// <summary>"2 of 4 done", "18 words to review".</summary>
    public string Headline { get; } = headline;

    public ObservableCollection<TodayItem> Items { get; } = [.. items];

    public bool HasItems => Items.Count > 0;

    /// <summary>The card's own button ("Review", "Open"); null: the whole card has none.</summary>
    public string? ActionText { get; } = actionText;

    public bool HasAction => !string.IsNullOrEmpty(ActionText);

    /// <summary>Overdue first: higher comes higher on the page.</summary>
    public int Urgency { get; } = urgency;

    [RelayCommand]
    private Task ActAsync() => act?.Invoke() ?? Task.CompletedTask;
}

/// <summary>What a tap on Home does, on each platform.</summary>
public interface IHomeHost
{
    /// <summary>Sends what was typed to Quick Capture (prefixes like /t, /n, /v pick the tool); returns where it went ("Added to Tracker").</summary>
    Task<string> CaptureAsync(string text);

    void OpenWhatsNew();
}

/// <summary>
/// Home is "today": capture a thought, what is wrong with Helm (only when something is), and what each tool has for
/// today, most urgent first, with small things done right here. Portable: each platform fills it from its tools.
/// </summary>
public sealed partial class HomeViewModel(IHomeHost host, string version, TimeProvider? time = null) : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private string _captureText = "";

    /// <summary>Where the last capture went ("Added to Tracker · Hôm nay"); cleared after a moment.</summary>
    [ObservableProperty] private string? _captureMessage;

    /// <summary>"Synced 2 minutes ago".</summary>
    [ObservableProperty] private string _syncLine = "";

    public string Version { get; } = version;

    public ObservableCollection<HomeAlert> Alerts { get; } = [];

    public ObservableCollection<TodayCard> Today { get; } = [];

    public bool HasAlerts => Alerts.Count > 0;

    public bool HasToday => Today.Count > 0;

    public bool IsAllClear => !HasAlerts && !HasToday;

    public string DateLine => _time.GetLocalNow().ToString("dddd, d MMMM", CultureInfo.CurrentCulture);

    public string Greeting => _time.GetLocalNow().Hour switch
    {
        < 5 => "Good night",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>"3 things need you today" — what is left to do, counted over the cards' items.</summary>
    public string Headline
    {
        get
        {
            var open = Today.Sum(c => c.Items.Count(i => !i.IsDone)) + Alerts.Count;
            return open switch
            {
                0 => "All clear for today",
                1 => "1 thing needs you today",
                _ => $"{open} things need you today",
            };
        }
    }

    public string Footer => string.IsNullOrEmpty(SyncLine) ? $"Helm {Version}" : $"Helm {Version} · {SyncLine}";

    partial void OnSyncLineChanged(string value) => OnPropertyChanged(nameof(Footer));

    public void SetAlerts(IEnumerable<HomeAlert> alerts)
    {
        Alerts.Clear();
        foreach (var alert in alerts.OrderBy(a => a.Level)) Alerts.Add(alert);
        Changed();
    }

    /// <summary>Most urgent first; a tool with nothing for today has no card.</summary>
    public void SetToday(IEnumerable<TodayCard> cards)
    {
        foreach (var card in Today)
            foreach (var item in card.Items) item.PropertyChanged -= OnItemChanged;
        Today.Clear();
        foreach (var card in cards.Where(c => c.HasItems || c.HasAction).OrderByDescending(c => c.Urgency))
        {
            foreach (var item in card.Items) item.PropertyChanged += OnItemChanged;
            Today.Add(card);
        }
        Changed();
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodayItem.IsDone)) OnPropertyChanged(nameof(Headline));
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(HasToday));
        OnPropertyChanged(nameof(IsAllClear));
        OnPropertyChanged(nameof(Headline));
    }

    private bool CanCapture() => CaptureText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        var text = CaptureText.Trim();
        CaptureText = "";
        var message = await host.CaptureAsync(text).ConfigureAwait(true);
        CaptureMessage = message;
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        if (CaptureMessage == message) CaptureMessage = null;
    }

    [RelayCommand]
    private void OpenWhatsNew() => host.OpenWhatsNew();
}
