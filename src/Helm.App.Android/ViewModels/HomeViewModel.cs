using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Helm.Shell.ViewModels;

namespace Helm.App.Android.ViewModels;

/// <summary>
/// Home on Android: the same tiles and lists as on Windows (What's new, Updates, Quick access, Utilities). Shortcuts
/// and shortcut conflicts are left out because Android tools have no keyboard shortcuts.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IModuleHost<IAndroidModule> _modules;
    private readonly IUpdateService _updates;
    private readonly ShellNavigator _navigator;
    private readonly IProcessLauncher _launcher;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly IReadOnlyDictionary<string, IModuleLauncher> _launchers;

    [ObservableProperty] private UpdateTile _updateTile;
    [ObservableProperty] private UtilitiesSortMode _sortMode;

    public HomeViewModel(
        IModuleHost<IAndroidModule> modules,
        IUpdateService updates,
        ShellNavigator navigator,
        IProcessLauncher launcher,
        IUiDispatcher ui,
        ISettingsStoreFactory settings,
        IEnumerable<IModuleLauncher> launchers)
    {
        _modules = modules;
        _updates = updates;
        _navigator = navigator;
        _launcher = launcher;
        _ui = ui;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _launchers = launchers.ToDictionary(l => l.ModuleId, StringComparer.Ordinal);
        _sortMode = _general.Current.UtilitiesSort;
        _updateTile = UpdateTile.From(updates);

        foreach (var module in modules.Modules) module.PropertyChanged += OnModuleChanged;
        updates.StateChanged += (_, _) => _ui.Post(() => UpdateTile = UpdateTile.From(_updates));

        RefreshUtilities();
        RefreshQuickAccess();
    }

    public string Version => $"v{AppInfo.Version}";

    public ObservableCollection<IAndroidModule> Utilities { get; } = [];

    public ObservableCollection<IAndroidModule> QuickAccess { get; } = [];

    public bool HasQuickAccess => QuickAccess.Count > 0;

    public bool HasUtilities => Utilities.Count > 0;

    /// <summary>Tile icon: green check, accent download or muted info; hidden while checking (a spinner shows).</summary>
    public bool IsToneOk => UpdateTile is { Tone: "ok", IsChecking: false };
    public bool IsToneAttention => UpdateTile is { Tone: "attention", IsChecking: false };
    public bool IsToneMuted => UpdateTile is { Tone: "muted", IsChecking: false };

    partial void OnUpdateTileChanged(UpdateTile value)
    {
        OnPropertyChanged(nameof(IsToneOk));
        OnPropertyChanged(nameof(IsToneAttention));
        OnPropertyChanged(nameof(IsToneMuted));
    }

    [RelayCommand]
    private void OpenModule(IAndroidModule? module)
    {
        if (module is not null) _navigator.GoModule(module);
    }

    /// <summary>Quick access tile: the module's own action when it has one, else its page.</summary>
    [RelayCommand]
    private async Task LaunchModuleAsync(IAndroidModule? module)
    {
        if (module is null) return;
        if (_launchers.TryGetValue(module.Id, out var launcher)) await launcher.LaunchAsync();
        else _navigator.GoModule(module);
    }

    [RelayCommand]
    private void ToggleSort()
    {
        SortMode = SortMode == UtilitiesSortMode.Alphabetical ? UtilitiesSortMode.Grouped : UtilitiesSortMode.Alphabetical;
        _general.Update(s => s.UtilitiesSort = SortMode);
        RefreshUtilities();
    }

    /// <summary>The release page of the running version (its notes are the CHANGELOG section).</summary>
    [RelayCommand]
    private void OpenWhatsNew() => _launcher.OpenUrl($"{AppInfo.ReleasesUrl}/tag/v{AppInfo.Version}");

    /// <summary>Tile tap: up to date → check again; anything needing attention → General.</summary>
    [RelayCommand]
    private async Task UpdateTileTapAsync()
    {
        if (_updates.State is UpdateState.Idle or UpdateState.UpToDate)
        {
            await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(true);
            return;
        }
        _navigator.GoGeneral();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) _ui.Post(RefreshQuickAccess);
    }

    private void RefreshUtilities()
    {
        IEnumerable<IAndroidModule> ordered = SortMode == UtilitiesSortMode.Grouped
            ? _modules.Modules.OrderBy(m => m.Group).ThenBy(m => m.DisplayName)
            : _modules.Modules.OrderBy(m => m.DisplayName);
        Replace(Utilities, ordered);
        OnPropertyChanged(nameof(HasUtilities));
    }

    private void RefreshQuickAccess()
    {
        Replace(QuickAccess, _modules.Modules.Where(m => m.IsEnabled).OrderBy(m => m.DisplayName));
        OnPropertyChanged(nameof(HasQuickAccess));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
