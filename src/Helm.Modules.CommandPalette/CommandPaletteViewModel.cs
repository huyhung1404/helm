using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.CommandPalette;

public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ISettingsStore<CommandPaletteSettings> _store;
    private bool _loading;

    [ObservableProperty] private HotkeyGesture _hotkey;
    [ObservableProperty] private bool _includeApps;
    [ObservableProperty] private bool _includeFiles;
    [ObservableProperty] private bool _includeWindows;
    [ObservableProperty] private bool _includeWindowsSettings;
    [ObservableProperty] private bool _includeWebSearch;
    [ObservableProperty] private int _webSearchIndex;

    public CommandPaletteViewModel(CommandPaletteModule module)
    {
        Module = module;
        _store = module.Settings;
        _loading = true;
        var s = _store.Current;
        Hotkey = s.Hotkey;
        IncludeApps = s.IncludeApps;
        IncludeFiles = s.IncludeFiles;
        IncludeWindows = s.IncludeWindows;
        IncludeWindowsSettings = s.IncludeWindowsSettings;
        IncludeWebSearch = s.IncludeWebSearch;
        WebSearchIndex = Math.Clamp((int)s.WebSearch, 0, WebSearchNames.Count - 1);
        _loading = false;
    }

    public CommandPaletteModule Module { get; }

    public HotkeyGesture DefaultHotkey => CommandPaletteSettings.DefaultHotkey;

    public IReadOnlyList<string> WebSearchNames => CommandPaletteSettings.WebSearchNames;

    [RelayCommand]
    private void Try() => Module.Open();

    partial void OnHotkeyChanged(HotkeyGesture value) => Save(s => s.Hotkey = value);

    partial void OnIncludeAppsChanged(bool value) => Save(s => s.IncludeApps = value);

    partial void OnIncludeFilesChanged(bool value) => Save(s => s.IncludeFiles = value);

    partial void OnIncludeWindowsChanged(bool value) => Save(s => s.IncludeWindows = value);

    partial void OnIncludeWindowsSettingsChanged(bool value) => Save(s => s.IncludeWindowsSettings = value);

    partial void OnIncludeWebSearchChanged(bool value) => Save(s => s.IncludeWebSearch = value);

    partial void OnWebSearchIndexChanged(int value)
    {
        if (value >= 0) Save(s => s.WebSearch = (WebSearchEngine)Math.Clamp(value, 0, WebSearchNames.Count - 1));
    }

    private void Save(Action<CommandPaletteSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }
}
