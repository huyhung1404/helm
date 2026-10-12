using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.Modules.ShortcutGuide;

/// <summary>One press of a shortcut in the library list, with "then" before every press after the first.</summary>
public sealed record KeyStep(IReadOnlyList<string> Caps, bool ShowThen);

/// <summary>A shortcut row on the library page.</summary>
public sealed class ShortcutRowViewModel
{
    public ShortcutRowViewModel(ShortcutGuideViewModel owner, GuideShortcut shortcut)
    {
        Shortcut = shortcut;
        Steps = shortcut.Keys.AllCaps().Select((caps, i) => new KeyStep(caps, i > 0)).ToList();
        ToggleHiddenCommand = new RelayCommand(() => owner.ToggleHidden(shortcut));
        DeleteCommand = new RelayCommand(() => owner.DeleteShortcut(shortcut));
    }

    public GuideShortcut Shortcut { get; }

    public IReadOnlyList<KeyStep> Steps { get; }

    public string Does => Shortcut.Does;

    public bool IsOwn => Shortcut.IsOwn;

    public bool IsBuiltIn => !Shortcut.IsOwn;

    public bool IsHidden => Shortcut.IsHidden;

    /// <summary>Hidden shortcuts stay in the list, faded, so they can be shown again.</summary>
    public double RowOpacity => IsHidden ? 0.45 : 1;

    public string HideToolTip => IsHidden ? "Show it in the guide again" : "Hide it from the guide";

    public Wpf.Ui.Controls.SymbolRegular HideIcon => IsHidden ? Wpf.Ui.Controls.SymbolRegular.Eye24 : Wpf.Ui.Controls.SymbolRegular.EyeOff24;

    public ICommand ToggleHiddenCommand { get; }

    public ICommand DeleteCommand { get; }
}

/// <summary>The shortcuts of the selected app that start with the same modifiers ("Ctrl+Shift").</summary>
public sealed record ShortcutGroupViewModel(string Title, IReadOnlyList<ShortcutRowViewModel> Rows);

/// <summary>
/// Shared by the settings page (the options) and the content page (the library: browse, add, hide). Every change goes
/// to the settings store; the module rebuilds its <see cref="ShortcutLibrary"/> and this view model reloads from it.
/// </summary>
public sealed partial class ShortcutGuideViewModel : ObservableObject
{
    private static readonly HotkeyModifiers[] s_groupOrder =
    [
        HotkeyModifiers.Ctrl, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, HotkeyModifiers.Alt, HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        HotkeyModifiers.Shift, HotkeyModifiers.Win, HotkeyModifiers.Win | HotkeyModifiers.Shift, HotkeyModifiers.Win | HotkeyModifiers.Ctrl,
        HotkeyModifiers.Win | HotkeyModifiers.Alt,
    ];

    private readonly ISettingsStore<ShortcutGuideSettings> _store;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    [ObservableProperty] private bool _holdCtrl;
    [ObservableProperty] private bool _holdAlt;
    [ObservableProperty] private bool _holdWin;
    [ObservableProperty] private bool _holdShift;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HoldDelayText))] private double _holdDelay;
    [ObservableProperty] private int _positionIndex;
    [ObservableProperty] private bool _includeGeneral;
    [ObservableProperty] private bool _hideInFullScreen;
    [ObservableProperty] private string _excludedAppsText = string.Empty;

    [ObservableProperty] private IReadOnlyList<GuideApp> _apps = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRemoveApp), nameof(AppDescription))] private GuideApp? _selectedApp;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private IReadOnlyList<ShortcutGroupViewModel> _groups = [];
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasNoMatches;

    [ObservableProperty] private string _newKeys = string.Empty;
    [ObservableProperty] private string _newDoes = string.Empty;
    [ObservableProperty] private string _newError = string.Empty;
    [ObservableProperty] private string _newAppName = string.Empty;
    [ObservableProperty] private string _newAppProcesses = string.Empty;
    [ObservableProperty] private string _newAppError = string.Empty;

    public ShortcutGuideViewModel(ShortcutGuideModule module, IUiDispatcher ui)
    {
        Module = module;
        _store = module.Settings;
        _ui = ui;
        _loading = true;
        var s = _store.Current;
        HoldCtrl = s.HoldCtrl;
        HoldAlt = s.HoldAlt;
        HoldWin = s.HoldWin;
        HoldShift = s.HoldShift;
        HoldDelay = Math.Clamp(s.HoldDelayMs, ShortcutGuideSettings.MinHoldDelayMs, ShortcutGuideSettings.MaxHoldDelayMs);
        PositionIndex = Math.Clamp((int)s.Position, 0, PositionNames.Count - 1);
        IncludeGeneral = s.IncludeGeneral;
        HideInFullScreen = s.HideInFullScreen;
        ExcludedAppsText = string.Join(Environment.NewLine, s.ExcludedApps);
        _loading = false;

        ReloadLibrary();
        module.LibraryChanged += (_, _) => _ui.Post(ReloadLibrary);
    }

    public ShortcutGuideModule Module { get; }

    public IReadOnlyList<string> PositionNames => ShortcutGuideSettings.PositionNames;

    public double MinHoldDelay => ShortcutGuideSettings.MinHoldDelayMs;

    public double MaxHoldDelay => ShortcutGuideSettings.MaxHoldDelayMs;

    public string HoldDelayText => $"{HoldDelay / 1000:0.0} s";

    public bool CanRemoveApp => SelectedApp?.IsOwn == true;

    public string AppDescription => SelectedApp switch
    {
        null => string.Empty,
        { Id: ShortcutLibrary.WindowsId } => "Shown over every app.",
        { Id: ShortcutLibrary.CommonId } => "Shortcuts most apps share, shown over every app.",
        { } app => $"Shown when {string.Join(", ", app.Processes.Select(p => p + ".exe"))} is in front.",
    };

    partial void OnHoldCtrlChanged(bool value) => Save(s => s.HoldCtrl = value);

    partial void OnHoldAltChanged(bool value) => Save(s => s.HoldAlt = value);

    partial void OnHoldWinChanged(bool value) => Save(s => s.HoldWin = value);

    partial void OnHoldShiftChanged(bool value) => Save(s => s.HoldShift = value);

    partial void OnHoldDelayChanged(double value) =>
        Save(s => s.HoldDelayMs = (int)Math.Clamp(Math.Round(value / 50) * 50, MinHoldDelay, MaxHoldDelay));

    partial void OnPositionIndexChanged(int value)
    {
        if (value >= 0) Save(s => s.Position = (GuidePosition)Math.Clamp(value, 0, PositionNames.Count - 1));
    }

    partial void OnIncludeGeneralChanged(bool value) => Save(s => s.IncludeGeneral = value);

    partial void OnHideInFullScreenChanged(bool value) => Save(s => s.HideInFullScreen = value);

    partial void OnExcludedAppsTextChanged(string value) => Save(s => s.ExcludedApps = ProcessExclusions.ParseLines(value));

    partial void OnSelectedAppChanged(GuideApp? value)
    {
        NewError = string.Empty;
        RebuildGroups();
    }

    partial void OnFilterChanged(string value) => RebuildGroups();

    /// <summary>Shows the guide for the selected app over this window, holding the modifiers it has most shortcuts for.</summary>
    [RelayCommand]
    private void Preview()
    {
        if (SelectedApp is not { } app) return;
        var lead = app.Visible.Where(s => s.Keys.HasLeadKey && s.Keys.LeadModifiers != HotkeyModifiers.None)
            .GroupBy(s => s.Keys.LeadModifiers)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        Module.Preview(app.Id, lead == HotkeyModifiers.None ? HotkeyModifiers.Ctrl : lead);
    }

    [RelayCommand]
    private void AddShortcut()
    {
        if (SelectedApp is not { } app) return;
        if (!ShortcutKeys.TryParse(NewKeys, out var keys, out var error))
        {
            NewError = error;
            return;
        }
        var does = NewDoes.Trim();
        if (does.Length == 0)
        {
            NewError = "Say what the shortcut does.";
            return;
        }
        _store.Update(s =>
        {
            // Adding the same keys again changes what they do.
            s.Shortcuts.RemoveAll(u => SameShortcut(u, app.Id, keys));
            s.Shortcuts.Add(new UserShortcut { AppId = app.Id, Keys = keys.Canonical, Does = does });
        });
        NewKeys = string.Empty;
        NewDoes = string.Empty;
        NewError = string.Empty;
    }

    [RelayCommand]
    private void AddApp()
    {
        var name = NewAppName.Trim();
        var processes = ProcessExclusions.ParseLines(NewAppProcesses).Select(ShortcutLibrary.NormalizeProcess).Where(p => p.Length > 0).ToList();
        if (name.Length == 0)
        {
            NewAppError = "Give the app a name.";
            return;
        }
        if (processes.Count == 0)
        {
            NewAppError = "Type the app's process name, e.g. blender.exe (Task Manager → Details shows it).";
            return;
        }
        var library = Module.Library;
        if (processes.Select(p => library.FindByProcess(p)).FirstOrDefault(a => a is not null) is { } existing)
        {
            NewAppError = $"That process is already listed under {existing.Name}: add the shortcuts there.";
            return;
        }
        var id = $"user-{Guid.NewGuid():N}"[..13];
        _store.Update(s => s.Apps.Add(new UserApp { Id = id, Name = name, Processes = processes }));
        NewAppName = string.Empty;
        NewAppProcesses = string.Empty;
        NewAppError = string.Empty;
        ReloadLibrary();
        SelectedApp = Apps.FirstOrDefault(a => a.Id == id) ?? SelectedApp;
    }

    /// <summary>Removes the selected app you added, with its shortcuts. The page asks first.</summary>
    public void RemoveSelectedApp()
    {
        if (SelectedApp is not { IsOwn: true } app) return;
        _store.Update(s =>
        {
            s.Apps.RemoveAll(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            s.Shortcuts.RemoveAll(u => string.Equals(u.AppId, app.Id, StringComparison.OrdinalIgnoreCase));
        });
        SelectedApp = null;
        ReloadLibrary();
    }

    internal void ToggleHidden(GuideShortcut shortcut)
    {
        if (shortcut.IsOwn) return;
        var key = ShortcutLibrary.HiddenKey(shortcut.AppId, shortcut.Keys);
        _store.Update(s =>
        {
            if (s.Hidden.RemoveAll(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase)) == 0) s.Hidden.Add(key);
        });
    }

    internal void DeleteShortcut(GuideShortcut shortcut)
    {
        if (!shortcut.IsOwn) return;
        _store.Update(s => s.Shortcuts.RemoveAll(u => SameShortcut(u, shortcut.AppId, shortcut.Keys)));
    }

    private static bool SameShortcut(UserShortcut shortcut, string appId, ShortcutKeys keys) =>
        string.Equals(shortcut.AppId, appId, StringComparison.OrdinalIgnoreCase) &&
        ShortcutKeys.TryParse(shortcut.Keys, out var parsed, out _) &&
        string.Equals(parsed.Canonical, keys.Canonical, StringComparison.OrdinalIgnoreCase);

    private void ReloadLibrary()
    {
        var selectedId = SelectedApp?.Id;
        var library = Module.Library;
        Apps = library.Apps;
        SelectedApp = Apps.FirstOrDefault(a => a.Id == selectedId) ?? Apps.FirstOrDefault(a => !a.IsGeneral) ?? Apps.FirstOrDefault();
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        if (SelectedApp is not { } app)
        {
            Groups = [];
            Summary = string.Empty;
            HasNoMatches = false;
            return;
        }
        var filter = Filter.Trim();
        var shown = app.Shortcuts.Where(s => filter.Length == 0 ||
                s.Does.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                s.Keys.Canonical.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Groups = shown.GroupBy(s => s.Keys.LeadModifiers)
            .OrderBy(g => GroupRank(g.Key))
            .Select(g => new ShortcutGroupViewModel(GroupTitle(g.Key), g.Select(s => new ShortcutRowViewModel(this, s)).ToList()))
            .ToList();
        var own = app.Shortcuts.Count(s => s.IsOwn);
        var hidden = app.Shortcuts.Count(s => s.IsHidden);
        Summary = $"{app.Shortcuts.Count} shortcuts" + (own > 0 ? $" · {own} yours" : string.Empty) + (hidden > 0 ? $" · {hidden} hidden" : string.Empty);
        HasNoMatches = shown.Count == 0;
    }

    private static int GroupRank(HotkeyModifiers modifiers)
    {
        if (modifiers == HotkeyModifiers.None) return int.MaxValue;
        var index = Array.IndexOf(s_groupOrder, modifiers);
        return index >= 0 ? index : s_groupOrder.Length + (int)modifiers;
    }

    private static string GroupTitle(HotkeyModifiers modifiers) =>
        modifiers == HotkeyModifiers.None ? "Without a modifier (not shown in the guide)" : string.Join(" + ", ShortcutStep.ModifierCaps(modifiers));

    private void Save(Action<ShortcutGuideSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }
}
