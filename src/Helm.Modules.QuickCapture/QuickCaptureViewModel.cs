using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.QuickCapture;

/// <summary>A line of the "What to type" card.</summary>
public sealed record CaptureExample(string Name, string Prefix, string Example);

public sealed partial class QuickCaptureViewModel : ObservableObject
{
    private readonly ISettingsStore<QuickCaptureSettings> _store;
    private bool _loading;

    [ObservableProperty] private HotkeyGesture _hotkey;
    [ObservableProperty] private int _defaultTargetIndex;

    public QuickCaptureViewModel(QuickCaptureModule module)
    {
        Module = module;
        _store = module.Settings;
        var targets = module.AllTargets;
        TargetNames = targets.Select(t => t.Name).ToList();
        _targetIds = targets.Select(t => t.Id).ToList();
        Examples = targets.Select(t => new CaptureExample(t.Name, "/" + t.Prefix, t.Example)).ToList();

        _loading = true;
        Hotkey = _store.Current.Hotkey;
        DefaultTargetIndex = Math.Max(0, _targetIds.IndexOf(_store.Current.DefaultTarget));
        _loading = false;
    }

    private readonly List<string> _targetIds;

    public QuickCaptureModule Module { get; }

    public HotkeyGesture DefaultHotkey => QuickCaptureSettings.DefaultHotkey;

    public IReadOnlyList<string> TargetNames { get; }

    public IReadOnlyList<CaptureExample> Examples { get; }

    [RelayCommand]
    private void Try() => Module.Open();

    partial void OnHotkeyChanged(HotkeyGesture value)
    {
        if (!_loading) _store.Update(s => s.Hotkey = value);
    }

    partial void OnDefaultTargetIndexChanged(int value)
    {
        if (!_loading && value >= 0 && value < _targetIds.Count) _store.Update(s => s.DefaultTarget = _targetIds[value]);
    }
}
