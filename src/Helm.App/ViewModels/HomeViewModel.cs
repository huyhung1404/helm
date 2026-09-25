using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.App.Services;
using Helm.App.Views.Pages;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Wpf.Ui.Controls;

namespace Helm.App.ViewModels;

internal sealed record ShortcutItem(SymbolRegular Icon, string Description, HotkeyGesture Gesture);

internal sealed partial class HomeViewModel : ObservableObject
{
    private readonly IModuleHost _modules;
    private readonly IHotkeyManager _hotkeys;
    private readonly IUpdateService _updates;
    private readonly IShellNavigator _navigator;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore<GeneralSettings> _general;

    [ObservableProperty]
    private int _conflictCount;

    [ObservableProperty]
    private string _conflictSummary = "No conflicts found";

    [ObservableProperty]
    private string _conflictDetails = string.Empty;

    [ObservableProperty]
    private string _updateTitle = "You're up to date";

    [ObservableProperty]
    private string _updateSubtitle = "Not checked yet";

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private UtilitiesSortMode _sortMode;

    public HomeViewModel(
        IModuleHost modules,
        IHotkeyManager hotkeys,
        IUpdateService updates,
        IShellNavigator navigator,
        IUiDispatcher ui,
        ISettingsStoreFactory settings)
    {
        _modules = modules;
        _hotkeys = hotkeys;
        _updates = updates;
        _navigator = navigator;
        _ui = ui;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _sortMode = _general.Current.UtilitiesSort;

        foreach (var module in modules.Modules) module.PropertyChanged += OnModuleChanged;
        hotkeys.ConflictsChanged += (_, _) => _ui.Post(RefreshConflicts);
        updates.StateChanged += (_, _) => _ui.Post(RefreshUpdateTile);

        RefreshAll();
    }

    public string Version => $"v{AppInfo.Version}";

    public ObservableCollection<IHelmModule> Utilities { get; } = [];

    public ObservableCollection<IHelmModule> QuickAccess { get; } = [];

    public ObservableCollection<ShortcutItem> Shortcuts { get; } = [];

    public bool HasQuickAccess => QuickAccess.Count > 0;

    public bool HasShortcuts => Shortcuts.Count > 0;

    public string SortTooltip => SortMode == UtilitiesSortMode.Alphabetical ? "Sorted alphabetically (click to group)" : "Grouped by category (click to sort A–Z)";

    [RelayCommand]
    private void OpenModule(IHelmModule? module)
    {
        if (module is not null) _navigator.Navigate(module.SettingsPageType);
    }

    [RelayCommand]
    private void ToggleSort()
    {
        SortMode = SortMode == UtilitiesSortMode.Alphabetical ? UtilitiesSortMode.Grouped : UtilitiesSortMode.Alphabetical;
        _general.Update(s => s.UtilitiesSort = SortMode);
        OnPropertyChanged(nameof(SortTooltip));
        RefreshUtilities();
    }

    [RelayCommand]
    private void OpenWhatsNew() => _navigator.Navigate(typeof(WhatsNewPage));

    [RelayCommand]
    private void OpenConflicts()
    {
        var first = _hotkeys.Conflicts.FirstOrDefault();
        var module = first is null ? null : _modules.Find(first.Definitions[0].ModuleId);
        if (module is not null) _navigator.Navigate(module.SettingsPageType);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateTitle = "Checking for updates…";
        try
        {
            await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsCheckingForUpdates = false;
            RefreshUpdateTile();
        }
    }

    public void RefreshAll()
    {
        RefreshUtilities();
        RefreshQuickAccess();
        RefreshShortcuts();
        RefreshConflicts();
        RefreshUpdateTile();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IHelmModule.IsEnabled) or nameof(IHelmModule.Hotkeys))
        {
            _ui.Post(() =>
            {
                RefreshQuickAccess();
                RefreshShortcuts();
            });
        }
    }

    private void RefreshUtilities()
    {
        IEnumerable<IHelmModule> ordered = SortMode == UtilitiesSortMode.Grouped
            ? _modules.Modules.OrderBy(m => m.Group).ThenBy(m => m.DisplayName)
            : _modules.Modules.OrderBy(m => m.DisplayName);
        Replace(Utilities, ordered);
    }

    private void RefreshQuickAccess()
    {
        Replace(QuickAccess, _modules.Modules.Where(m => m.IsEnabled).OrderBy(m => m.DisplayName));
        OnPropertyChanged(nameof(HasQuickAccess));
    }

    private void RefreshShortcuts()
    {
        var items = _modules.Modules
            .Where(m => m.IsEnabled)
            .OrderBy(m => m.DisplayName)
            .SelectMany(m => m.Hotkeys
                .Where(h => h.Gesture.Key != 0 || h.Gesture.Modifiers != HotkeyModifiers.None)
                .Select(h => new ShortcutItem(m.Icon, h.Description, h.Gesture)));
        Replace(Shortcuts, items);
        OnPropertyChanged(nameof(HasShortcuts));
    }

    private void RefreshConflicts()
    {
        var conflicts = _hotkeys.Conflicts;
        ConflictCount = conflicts.Count;
        ConflictSummary = conflicts.Count switch
        {
            0 => "No conflicts found",
            1 => "1 conflict found",
            var n => $"{n} conflicts found",
        };
        ConflictDetails = conflicts.Count == 0 ? "All shortcuts are registered." : string.Join(Environment.NewLine, conflicts.Select(c => c.Describe()));
    }

    private void RefreshUpdateTile()
    {
        var result = _updates.LastResult;
        UpdateTitle = result?.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => $"Update available · v{result.Version}",
            UpdateCheckStatus.Failed => "Couldn't check for updates",
            UpdateCheckStatus.NotInstalled => "Updates unavailable",
            _ => "You're up to date",
        };
        UpdateSubtitle = _updates.LastChecked is { } checkedAt
            ? $"Last checked: {FormatWhen(checkedAt)}"
            : "Not checked yet";
    }

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var day = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("d");
        return $"{day} at {local:t}";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
