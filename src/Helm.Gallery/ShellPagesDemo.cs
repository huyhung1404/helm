using Avalonia.Controls;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Shell.Diagnostics;
using Helm.Shell.Pages;

namespace Helm.Gallery;

/// <summary>Which Diagnostics a Gallery entry shows.</summary>
internal enum DiagnosticsStage
{
    /// <summary>Two monitors, some windows, no shortcut taken.</summary>
    ThisPc,
    /// <summary>A shortcut taken and the hooks test running.</summary>
    Watching,
}

/// <summary>The shell's small pages, Welcome and Diagnostics, as the real views over pretend facts.</summary>
internal static class ShellPagesDemo
{
    public static Control Welcome() => new WelcomeView(() => { });

    public static Control Diagnostics(DiagnosticsStage stage) => BuildDiagnostics(stage).View;

    /// <summary>The Diagnostics view with its view model and pretend source (the UI tests drive them).</summary>
    public static (DiagnosticsView View, DiagnosticsViewModel ViewModel, PretendDiagnostics Source, MemoryClipboard Clipboard) BuildDiagnostics(DiagnosticsStage stage)
    {
        var source = new PretendDiagnostics(stage == DiagnosticsStage.Watching);
        var clipboard = new MemoryClipboard();
        var vm = new DiagnosticsViewModel(source, clipboard, new AvaloniaUi());
        if (stage == DiagnosticsStage.Watching)
        {
            vm.IsHookTestEnabled = true;
            source.Key("K down");
            source.Mouse("LeftButtonDown at (1820, 644)");
        }
        return (new DiagnosticsView(vm), vm, source, clipboard);
    }
}

/// <summary>The clipboard, in memory.</summary>
internal sealed class MemoryClipboard : Helm.Shell.Services.IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}

/// <summary>A pretend computer for Diagnostics: two monitors, a few windows, hooks driven by the demo.</summary>
internal sealed class PretendDiagnostics(bool conflict) : IDiagnosticsSource
{
    private Action<string>? _key;
    private Action<string>? _mouse;

    /// <summary>The folders "Open" was asked for (the UI tests read it).</summary>
    public List<string> Opened { get; } = [];

    public bool Watching => _key is not null;

    public IReadOnlyList<DiagnosticsFact> Facts() =>
    [
        new("Version", $"Helm {Helm.Shell.Services.AppInfo.Version}"),
        new("Program", @"C:\Users\hung\AppData\Local\HelmApp\current\Helm.exe"),
        new("Data folder", @"C:\Users\hung\AppData\Roaming\Helm", IsFolder: true),
        new("Settings", @"C:\Users\hung\AppData\Roaming\Helm\settings", IsFolder: true),
        new("Logs", @"C:\Users\hung\AppData\Roaming\Helm\logs", IsFolder: true),
        new("Runs as administrator", "Yes"),
        new("Windows", "Microsoft Windows 10.0.26200"),
        new(".NET", ".NET 8.0.22"),
    ];

    public IReadOnlyList<MonitorInfo> Monitors() =>
    [
        new(1, "DISPLAY1_2560x1440", @"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), 120, true),
        new(2, "DISPLAY2_1920x1080", @"\\.\DISPLAY2", new PixelRect(2560, 180, 4480, 1260), new PixelRect(2560, 180, 4480, 1260), 96, false),
    ];

    public IReadOnlyList<WindowInfo> Windows() =>
    [
        new(0x1A0B2, "DiagnosticsView.axaml — Helm — Visual Studio Code", "Chrome_WidgetWin_1", 4120, "Code", new PixelRect(0, 0, 1485, 1392), false, false, false, false),
        new(0x2C0F4, "BubblePop - Main.unity - Android - Unity 2022.3.45f1", "UnityContainerWndClass", 8840, "Unity", new PixelRect(2560, 180, 4480, 1260), true, false, false, false),
        new(0x3D11A, "Slack | #release-bubble-pop | Bubble Studio", "Chrome_WidgetWin_1", 1312, "slack", new PixelRect(1485, 0, 2560, 696), false, false, false, false),
        new(0x4E20C, "Calculator", "ApplicationFrameWindow", 9920, "ApplicationFrameHost", new PixelRect(2200, 820, 2540, 1340), false, false, true, false),
        new(0x5F31E, "Spotify Premium", "Chrome_WidgetWin_0", 6604, "Spotify", new PixelRect(0, 0, 1280, 800), false, true, false, false),
    ];

    public IReadOnlyList<string> Conflicts() =>
        conflict ? ["Ctrl+Alt+4 (Open or close zone 4) is already used by another application."] : [];

    public (nint Handle, string Title) Foreground() => (0x1A0B2, "DiagnosticsView.axaml — Helm — Visual Studio Code");

    public IDisposable WatchInput(Action<string> key, Action<string> mouse)
    {
        _key = key;
        _mouse = mouse;
        return new Stop(() => _key = _mouse = null);
    }

    public void OpenFolder(string path) => Opened.Add(path);

    public void Key(string text) => _key?.Invoke(text);

    public void Mouse(string text) => _mouse?.Invoke(text);

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
