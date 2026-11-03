using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Settings;

namespace Helm.Ai.Assistant;

/// <summary>What the "Ways to open" page needs from the phone.</summary>
public interface IAskHelmOpenPlatform
{
    /// <summary>"Display over other apps" is allowed for Helm.</summary>
    bool CanDrawOverlays();

    /// <summary>Opens the phone's "Display over other apps" page for Helm.</summary>
    void OpenOverlaySettings();

    /// <summary>The launcher can place the widget when Helm asks (Android 8+, most launchers).</summary>
    bool CanPinWidget();

    /// <summary>Asks the launcher to add the Ask Helm widget; it shows its own confirmation.</summary>
    void PinWidget();

    /// <summary>The Ask Helm tile is in the Quick Settings panel (as far as Helm saw).</summary>
    bool TileAdded();
}

/// <summary>
/// Quick Capture › Ways to open (phone): the edge handle (on, side, height, position, hidden over full-screen apps), the
/// home-screen widget, the "Ask Helm" icon, and the Quick Settings tile's state. Changes are saved at once in
/// <see cref="AskHelmOpenSettings"/>; the phone applies them from the store's Changed event.
/// </summary>
public sealed partial class AskHelmWaysViewModel : ObservableObject
{
    private readonly ISettingsStore<AskHelmOpenSettings> _store;
    private readonly IAskHelmOpenPlatform _platform;

    [ObservableProperty] private bool _canDraw;
    [ObservableProperty] private bool _tileAdded;

    public AskHelmWaysViewModel(ISettingsStoreFactory settings, IAskHelmOpenPlatform platform)
    {
        _store = settings.Get<AskHelmOpenSettings>(AskHelmOpenSettings.StoreId);
        _platform = platform;
        Refresh();
    }

    private AskHelmOpenSettings S => _store.Current;

    public bool EdgeHandle
    {
        get => S.EdgeHandle;
        set
        {
            if (value == S.EdgeHandle) return;
            Save(s => s.EdgeHandle = value, nameof(EdgeHandle));
            // Turned on without the permission: the phone's page to allow it, then back here.
            if (value && !CanDraw) _platform.OpenOverlaySettings();
        }
    }

    public bool IsLeft
    {
        get => S.EdgeSide == EdgeSide.Left;
        set { if (value) SetSide(EdgeSide.Left); }
    }

    public bool IsRight
    {
        get => S.EdgeSide == EdgeSide.Right;
        set { if (value) SetSide(EdgeSide.Right); }
    }

    /// <summary>The slider's value (48–160, steps of 8).</summary>
    public double HeightDp
    {
        get => S.EdgeHeightDp;
        set
        {
            var dp = AskHelmOpenSettings.ClampHeight((int)Math.Round(value));
            if (dp != S.EdgeHeightDp) Save(s => s.EdgeHeightDp = dp, nameof(HeightDp), nameof(HeightText));
        }
    }

    public string HeightText => $"{S.EdgeHeightDp}dp";

    /// <summary>The slider's value (5–95).</summary>
    public double PositionPercent
    {
        get => S.EdgePositionPercent;
        set
        {
            var percent = AskHelmOpenSettings.ClampPosition((int)Math.Round(value));
            if (percent != S.EdgePositionPercent) Save(s => s.EdgePositionPercent = percent, nameof(PositionPercent), nameof(PositionText));
        }
    }

    public string PositionText => $"{S.EdgePositionPercent}% down";

    public bool HideOverFullScreen
    {
        get => S.HideOverFullScreen;
        set { if (value != S.HideOverFullScreen) Save(s => s.HideOverFullScreen = value, nameof(HideOverFullScreen)); }
    }

    public bool LauncherIcon
    {
        get => S.LauncherIcon;
        set { if (value != S.LauncherIcon) Save(s => s.LauncherIcon = value, nameof(LauncherIcon)); }
    }

    public double MinHeight => AskHelmOpenSettings.MinHeightDp;
    public double MaxHeight => AskHelmOpenSettings.MaxHeightDp;
    public double HeightStep => AskHelmOpenSettings.HeightStepDp;
    public double MinPosition => AskHelmOpenSettings.MinPositionPercent;
    public double MaxPosition => AskHelmOpenSettings.MaxPositionPercent;

    public bool NeedsPermission => !CanDraw;

    public string PermissionText => CanDraw ? "Needs “Display over other apps”. Granted." : "Needs “Display over other apps”. Not allowed yet.";

    /// <summary>The widget row's second line: what tapping it does on this phone.</summary>
    public string WidgetText => _platform.CanPinWidget() ? "Aa, mic and camera on your home screen" : "Long-press the home screen › Widgets › Helm";

    public string TileText => TileAdded ? "Added" : "Not added: swipe down twice, tap the pencil and drag “Ask Helm” into the panel";

    /// <summary>Reads the phone's state again (the page is shown, or the user came back from the phone's settings).</summary>
    public void Refresh()
    {
        CanDraw = _platform.CanDrawOverlays();
        TileAdded = _platform.TileAdded();
        OnPropertyChanged(nameof(WidgetText));
    }

    partial void OnCanDrawChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsPermission));
        OnPropertyChanged(nameof(PermissionText));
    }

    partial void OnTileAddedChanged(bool value) => OnPropertyChanged(nameof(TileText));

    [RelayCommand]
    private void AllowDraw() => _platform.OpenOverlaySettings();

    [RelayCommand]
    private void AddWidget()
    {
        if (_platform.CanPinWidget()) _platform.PinWidget();
    }

    private void SetSide(EdgeSide side)
    {
        if (side == S.EdgeSide) return;
        Save(s => s.EdgeSide = side, nameof(IsLeft), nameof(IsRight));
    }

    private void Save(Action<AskHelmOpenSettings> change, params string[] changed)
    {
        _store.Update(change);
        foreach (var name in changed) OnPropertyChanged(name);
    }
}
