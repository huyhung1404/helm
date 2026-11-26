using System.ComponentModel;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.AlwaysOnTop;
using Helm.Modules.AlwaysOnTop.Ui;

namespace Helm.Gallery;

/// <summary>Which Always On Top a Gallery entry shows.</summary>
internal enum AlwaysOnTopStage
{
    /// <summary>Three windows kept on top.</summary>
    Pinned,
    /// <summary>Nothing on top yet.</summary>
    Empty,
    /// <summary>Another app took the shortcut.</summary>
    ShortcutTaken,
}

/// <summary>The real Always On Top view and view model over settings in a temporary folder and pretend kept windows.</summary>
internal static class AlwaysOnTopDemo
{
    public static Avalonia.Controls.Control Create(FakeModule module, AlwaysOnTopStage stage) => Build(module, stage).View;

    /// <summary>The view with its view model and pretend tool (the UI tests drive them).</summary>
    public static (AlwaysOnTopView View, AlwaysOnTopViewModel ViewModel, PretendAlwaysOnTop Tool) Build(FakeModule module, AlwaysOnTopStage stage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var tool = new PretendAlwaysOnTop(module, new SettingsStoreFactory(new HelmPaths(dir)), stage == AlwaysOnTopStage.Empty ? [] : Windows());
        if (stage == AlwaysOnTopStage.Pinned)
            tool.Settings.Update(s =>
            {
                s.UseAccentColor = false;
                s.BorderColor = "#FFB900";
            });
        if (stage == AlwaysOnTopStage.ShortcutTaken)
            tool.StatusMessage = "The shortcut Win+Ctrl+T is not active: another app uses it. Pick other keys below.";
        var vm = new AlwaysOnTopViewModel(tool, new AvaloniaUi());
        return (new AlwaysOnTopView(vm, new Host()), vm, tool);
    }

    private static List<PinnedWindowInfo> Windows() =>
    [
        new(0x2001, "Calculator", "CalculatorApp.exe"),
        new(0x2002, "BubblePop - Game view - Unity 2022.3.45f1", "Unity.exe"),
        new(0x2003, "Picture in picture — YouTube — Google Chrome", "chrome.exe"),
    ];

    private sealed class Host : IAlwaysOnTopPageHost;
}

/// <summary>Always On Top, pretended: pretend kept windows; Unpin only changes this list. On/off follows the Gallery's switch.</summary>
internal sealed class PretendAlwaysOnTop : IAlwaysOnTop
{
    private readonly FakeModule _module;
    private readonly List<PinnedWindowInfo> _pinned;
    private string? _status;

    public PretendAlwaysOnTop(FakeModule module, ISettingsStoreFactory settings, List<PinnedWindowInfo> pinned)
    {
        _module = module;
        _pinned = pinned;
        Settings = settings.Get<AlwaysOnTopSettings>(AlwaysOnTopIds.ModuleId);
        module.PropertyChanged += (_, e) =>
        {
            PropertyChanged?.Invoke(this, e);
            if (e.PropertyName == nameof(IModule.IsEnabled)) PinnedWindowsChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public string Id => AlwaysOnTopIds.ModuleId;
    public string DisplayName => "Always On Top";
    public string Description => "";
    public ModuleGroup Group => ModuleGroup.WindowsAndDesktop;

    public bool IsEnabled
    {
        get => _module.IsEnabled;
        set => _module.IsEnabled = value;
    }

    public string? StatusMessage
    {
        get => _status;
        set
        {
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
        }
    }

    public ISettingsStore<AlwaysOnTopSettings> Settings { get; }

    public IReadOnlyList<PinnedWindowInfo> PinnedWindows => IsEnabled ? [.. _pinned] : [];

    public event EventHandler? PinnedWindowsChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task EnableAsync(CancellationToken ct) => Task.CompletedTask;

    public Task DisableAsync() => Task.CompletedTask;

    public void Unpin(nint hwnd)
    {
        _pinned.RemoveAll(p => p.Handle == hwnd);
        PinnedWindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task UnpinAllAsync()
    {
        _pinned.Clear();
        PinnedWindowsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
