using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Capture;
using Helm.Core.Settings;

namespace Helm.Modules.QuickCapture;

/// <summary>A line of the "What to type" card.</summary>
public sealed record CaptureExample(string Name, string Prefix, string Example);

public sealed partial class QuickCaptureViewModel : ObservableObject
{
    private readonly ISettingsStore<QuickCaptureSettings> _store;
    private readonly List<string> _targetIds;
    private readonly bool _loading;

    [ObservableProperty] private int _defaultTargetIndex;

    /// <summary>What happened after "Add to Quick Settings".</summary>
    [ObservableProperty] private string? _message;

    public QuickCaptureViewModel(QuickCaptureModule module, IEnumerable<ICaptureTarget> targets, ISettingsStoreFactory settings)
    {
        Module = module;
        _store = settings.Get<QuickCaptureSettings>(QuickCaptureModule.ModuleId);
        var all = targets.OrderBy(t => t.Order).ToList();
        TargetNames = all.Select(t => t.Name).ToList();
        _targetIds = all.Select(t => t.Id).ToList();
        Examples = all.Select(t => new CaptureExample(t.Name, "/" + t.Prefix, t.Example)).ToList();
        _loading = true;
        DefaultTargetIndex = Math.Max(0, _targetIds.IndexOf(_store.Current.DefaultTarget));
        _loading = false;
    }

    public QuickCaptureModule Module { get; }

    public IReadOnlyList<string> TargetNames { get; }

    public IReadOnlyList<CaptureExample> Examples { get; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnDefaultTargetIndexChanged(int value)
    {
        if (!_loading && value >= 0 && value < _targetIds.Count) _store.Update(s => s.DefaultTarget = _targetIds[value]);
    }
}
