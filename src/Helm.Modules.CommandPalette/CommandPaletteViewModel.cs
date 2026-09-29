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

    public CommandPaletteViewModel(CommandPaletteModule module)
    {
        Module = module;
        _store = module.Settings;
        _loading = true;
        Hotkey = _store.Current.Hotkey;
        IncludeApps = _store.Current.IncludeApps;
        _loading = false;
    }

    public CommandPaletteModule Module { get; }

    public HotkeyGesture DefaultHotkey => CommandPaletteSettings.DefaultHotkey;

    [RelayCommand]
    private void Try() => Module.Open();

    partial void OnHotkeyChanged(HotkeyGesture value) => Save(s => s.Hotkey = value);

    partial void OnIncludeAppsChanged(bool value) => Save(s => s.IncludeApps = value);

    private void Save(Action<CommandPaletteSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }
}
