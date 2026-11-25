using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Home;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.General;

/// <summary>One part of General: a row of the list on the left (a phone: the list itself), with how it stands now.</summary>
public sealed partial class GeneralSection(string id, string title, Symbol icon) : ObservableObject
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public Symbol Icon { get; } = icon;

    /// <summary>Its own picture, drawn like a tool icon (<see cref="GeneralIcons"/>).</summary>
    public Avalonia.Media.IImage? Image { get; } = GeneralIcons.For(id);

    /// <summary>"14 of 17 on", "Synced 2 minutes ago": the state, so most visits end at the list.</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>Something here needs the user (sync stuck, an update failed): the summary turns red.</summary>
    [ObservableProperty] private bool _needsAttention;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>A tool's data, synced or not on this device.</summary>
public sealed partial class SyncTarget(string name, bool isSynced) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private bool _isSynced = isSynced;
}

/// <summary>A device of the sync account.</summary>
public sealed partial class SyncDevice(string name, string details, bool isThis, Func<SyncDevice, Task> remove) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>"Last seen 2 minutes ago · Windows".</summary>
    public string Details { get; } = details;

    public bool IsThis { get; } = isThis;

    public bool CanRemove => !IsThis;

    [RelayCommand]
    private Task RemoveAsync() => remove(this);
}

/// <summary>A tool working while its page is closed (a reminder timer, a sync relay).</summary>
public sealed partial class BackgroundJob(string title, string detail, string since, Func<BackgroundJob, Task> stop) : ObservableObject
{
    public string Title { get; } = title;

    public string Detail { get; } = detail;

    /// <summary>"Since 08:12".</summary>
    public string Since { get; } = since;

    [RelayCommand]
    private Task StopAsync() => stop(this);
}

/// <summary>What a button in General does, on each platform (dialogs, files, the update service, the sync engine).</summary>
public interface IGeneralHost
{
    Task SyncNowAsync();

    Task PairDeviceAsync(string code);

    Task ChangePassphraseAsync();

    Task TurnOffSyncAsync();

    Task UpdateAsync();

    void OpenSettingsFolder();

    void OpenLogs();

    Task ResetAllAsync();
}

/// <summary>
/// General, in parts instead of one long page: Tools (on or off), Sync, Updates, Appearance &amp; startup, Running in the
/// background, Help &amp; troubleshooting. Each part says how it stands in the list, so most visits end there. Portable:
/// each platform fills it and answers its buttons (<see cref="IGeneralHost"/>).
/// </summary>
public sealed partial class GeneralViewModel : ObservableObject
{
    private readonly IGeneralHost _host;

    [ObservableProperty] private GeneralSection _selected;
    [ObservableProperty] private bool _groupByCategory;

    // Sync
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private string _syncServer = "";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string _usage = "";

    // Updates
    [ObservableProperty] private string _updateTitle = "You're up to date";
    [ObservableProperty] private string _updateSubtitle = "";
    [ObservableProperty] private string _updateAction = "Check for updates";
    [ObservableProperty] private int _channelIndex;
    [ObservableProperty] private bool _autoDownload = true;
    [ObservableProperty] private bool _installOnRestart = true;
    [ObservableProperty] private bool _deleteSettingsOnUninstall;
    [ObservableProperty] private bool _runAsAdministrator;

    // Appearance & startup
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _runAtStartup = true;
    [ObservableProperty] private bool _startMinimized = true;

    public GeneralViewModel(IGeneralHost host, string version, bool isDesktop)
    {
        _host = host;
        Version = version;
        IsDesktop = isDesktop;
        Sections =
        [
            new("tools", "Tools", Symbol.Apps),
            new("sync", "Sync", Symbol.ArrowSync),
            new("updates", "Updates", Symbol.ArrowCircleDown),
            new("appearance", isDesktop ? "Appearance & startup" : "Appearance", Symbol.PaintBrush),
            new("background", "Running in background", Symbol.Pulse),
            new("help", "Help & troubleshooting", Symbol.QuestionCircle),
        ];
        _selected = Sections[0];
        _selected.IsSelected = true;
        Tools.CollectionChanged += (_, _) => Refresh();
        Jobs.CollectionChanged += (_, _) => Refresh();
    }

    public string Version { get; }

    /// <summary>Windows (and later macOS): startup, tray, administrator mode, the update channel's install-on-restart.</summary>
    public bool IsDesktop { get; }

    public IReadOnlyList<GeneralSection> Sections { get; }

    public ObservableCollection<HomeTool> Tools { get; } = [];

    public ObservableCollection<SyncTarget> SyncTargets { get; } = [];

    public ObservableCollection<SyncDevice> Devices { get; } = [];

    public ObservableCollection<BackgroundJob> Jobs { get; } = [];

    public IReadOnlyList<string> Channels { get; } = ["Stable", "Preview"];

    public IReadOnlyList<string> Themes { get; } = ["Same as the system", "Light", "Dark"];

    public string SettingsFolder { get; set; } = "";

    public bool IsTools => Selected.Id == "tools";

    public bool IsSync => Selected.Id == "sync";

    public bool IsUpdates => Selected.Id == "updates";

    public bool IsAppearance => Selected.Id == "appearance";

    public bool IsBackground => Selected.Id == "background";

    public bool IsHelp => Selected.Id == "help";

    public bool HasJobs => Jobs.Count > 0;

    public string SortLabel => GroupByCategory ? "By category" : "A–Z";

    public GeneralSection Section(string id) => Sections.First(s => s.Id == id);

    partial void OnSelectedChanged(GeneralSection? oldValue, GeneralSection newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        foreach (var name in new[] { nameof(IsTools), nameof(IsSync), nameof(IsUpdates), nameof(IsAppearance), nameof(IsBackground), nameof(IsHelp) })
            OnPropertyChanged(name);
    }

    partial void OnGroupByCategoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SortLabel));
        Order();
    }

    partial void OnThemeIndexChanged(int value) => Refresh();

    partial void OnRunAtStartupChanged(bool value) => Refresh();

    partial void OnUpdateTitleChanged(string value) => Refresh();

    partial void OnSyncStatusChanged(string value) => Refresh();

    public void SetTools(IEnumerable<HomeTool> tools)
    {
        foreach (var tool in Tools) tool.PropertyChanged -= OnToolChanged;
        Tools.Clear();
        foreach (var tool in tools)
        {
            tool.PropertyChanged += OnToolChanged;
            Tools.Add(tool);
        }
        Order();
    }

    private void OnToolChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeTool.IsEnabled)) Refresh();
    }

    private void Order()
    {
        var ordered = (GroupByCategory
            ? Tools.OrderBy(t => t.Group, StringComparer.CurrentCulture).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            : Tools.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        // Moved in place: a switch under the pointer is not rebuilt.
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Tools.IndexOf(ordered[i]);
            if (at != i) Tools.Move(at, i);
        }
        Refresh();
    }

    /// <summary>The state line of each part, in the list.</summary>
    public void Refresh()
    {
        Section("tools").Summary = $"{Tools.Count(t => t.IsEnabled)} of {Tools.Count} on";
        Section("sync").Summary = SyncStatus;
        Section("updates").Summary = $"{Version} · {UpdateTitle}";
        Section("appearance").Summary = Themes[Math.Clamp(ThemeIndex, 0, Themes.Count - 1)] + (IsDesktop && RunAtStartup ? " · starts with Windows" : "");
        Section("background").Summary = Jobs.Count switch { 0 => "Nothing running", 1 => "1 tool running", var n => $"{n} tools running" };
        Section("help").Summary = "Logs, settings folder, reset";
        OnPropertyChanged(nameof(HasJobs));
    }

    [RelayCommand]
    private void Show(GeneralSection? section)
    {
        if (section is not null) Selected = section;
    }

    [RelayCommand]
    private void ToggleSort() => GroupByCategory = !GroupByCategory;

    [RelayCommand]
    private Task SyncNowAsync() => _host.SyncNowAsync();

    [RelayCommand]
    private async Task PairAsync()
    {
        var code = PairingCode.Trim();
        if (code.Length == 0) return;
        await _host.PairDeviceAsync(code).ConfigureAwait(true);
        PairingCode = "";
    }

    [RelayCommand]
    private Task ChangePassphraseAsync() => _host.ChangePassphraseAsync();

    [RelayCommand]
    private Task TurnOffSyncAsync() => _host.TurnOffSyncAsync();

    [RelayCommand]
    private Task UpdateAsync() => _host.UpdateAsync();

    [RelayCommand]
    private void OpenSettingsFolder() => _host.OpenSettingsFolder();

    [RelayCommand]
    private void OpenLogs() => _host.OpenLogs();

    [RelayCommand]
    private Task ResetAllAsync() => _host.ResetAllAsync();
}
