using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Shell.Services;

namespace Helm.Shell.Diagnostics;

/// <summary>A line of "This Helm": its name, its value and whether the value is a folder that can be opened.</summary>
public sealed record DiagnosticsFact(string Name, string Value, bool IsFolder = false);

/// <summary>A monitor as the Diagnostics page lists it.</summary>
public sealed record DiagnosticsMonitor(string Name, string Bounds, string WorkArea, string Scale, bool IsPrimary)
{
    public static DiagnosticsMonitor From(MonitorInfo m) =>
        new(m.DisplayName, m.Bounds.ToString(), m.WorkArea.ToString(), $"{m.Dpi} DPI ({m.Scale:P0})", m.IsPrimary);
}

/// <summary>A top-level window as the Diagnostics page lists it.</summary>
public sealed record DiagnosticsWindow(string Handle, string Process, string Title, string Bounds, string Flags)
{
    public static DiagnosticsWindow From(WindowInfo w)
    {
        var flags = new List<string>();
        if (w.IsMaximized) flags.Add("maximized");
        if (w.IsMinimized) flags.Add("minimized");
        if (w.IsTopmost) flags.Add("on top");
        if (w.IsCloaked) flags.Add("cloaked");
        return new(w.HandleHex, w.ProcessName, string.IsNullOrWhiteSpace(w.Title) ? "(no title)" : w.Title, w.Bounds.ToString(), string.Join(" · ", flags));
    }
}

/// <summary>What only the platform knows for the Diagnostics page (on Windows: Win32, the hooks, the hotkey manager).</summary>
public interface IDiagnosticsSource
{
    /// <summary>The version, the folders and the like ("This Helm").</summary>
    IReadOnlyList<DiagnosticsFact> Facts();

    IReadOnlyList<MonitorInfo> Monitors();

    /// <summary>The top-level app windows, as Helm's tools see them.</summary>
    IReadOnlyList<WindowInfo> Windows();

    /// <summary>Shortcuts that are taken, in words.</summary>
    IReadOnlyList<string> Conflicts();

    /// <summary>The window in front: its handle and title.</summary>
    (nint Handle, string Title) Foreground();

    /// <summary>
    /// Starts watching the keyboard and the mouse (the low-level hooks); each event is told in words, on any thread.
    /// Throws <see cref="InvalidOperationException"/> with a message to show when the hooks cannot be installed.
    /// </summary>
    IDisposable WatchInput(Action<string> key, Action<string> mouse);

    void OpenFolder(string path);
}

/// <summary>
/// The Diagnostics page: this Helm (version, folders), the input hooks test, the shortcuts taken, the monitors and the
/// windows as Helm sees them; everything can be copied for a bug report.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private const string Idle = "Off. Turn it on, then type or move the mouse anywhere.";

    private readonly IDiagnosticsSource _source;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _ui;
    private IDisposable? _watch;
    private long _keys;
    private long _moves;

    [ObservableProperty] private bool _isHookTestEnabled;
    [ObservableProperty] private string _hookStatus = Idle;
    [ObservableProperty] private string _lastKey = "–";
    [ObservableProperty] private string _lastMouse = "–";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _copied = string.Empty;

    public DiagnosticsViewModel(IDiagnosticsSource source, IClipboardService clipboard, IUiDispatcher ui)
    {
        _source = source;
        _clipboard = clipboard;
        _ui = ui;
        Refresh();
    }

    public ObservableCollection<DiagnosticsFact> Facts { get; } = [];

    public ObservableCollection<DiagnosticsMonitor> Monitors { get; } = [];

    public ObservableCollection<DiagnosticsWindow> Windows { get; } = [];

    public ObservableCollection<string> Conflicts { get; } = [];

    public bool HasConflicts => Conflicts.Count > 0;

    [RelayCommand]
    private void Refresh()
    {
        Replace(Facts, _source.Facts());
        Replace(Monitors, _source.Monitors().Select(DiagnosticsMonitor.From));
        Replace(Windows, _source.Windows().Select(DiagnosticsWindow.From));
        Replace(Conflicts, _source.Conflicts());
        OnPropertyChanged(nameof(HasConflicts));

        var (handle, title) = _source.Foreground();
        var monitors = Monitors.Count == 1 ? "1 monitor" : $"{Monitors.Count} monitors";
        var windows = Windows.Count == 1 ? "1 app window" : $"{Windows.Count} app windows";
        Summary = $"{monitors}, {windows}. In front: 0x{handle:X} {title}".TrimEnd();
    }

    /// <summary>One value to the clipboard (a fact's value, a window's line).</summary>
    [RelayCommand]
    private void Copy(DiagnosticsFact? fact)
    {
        if (fact is null) return;
        _clipboard.SetText(fact.Value);
        Copied = $"Copied: {fact.Name}";
    }

    [RelayCommand]
    private void Open(DiagnosticsFact? fact)
    {
        if (fact is { IsFolder: true }) _source.OpenFolder(fact.Value);
    }

    /// <summary>Everything on the page as text, for a bug report.</summary>
    [RelayCommand]
    private void CopyAll()
    {
        _clipboard.SetText(Report());
        Copied = "Copied: the whole page";
    }

    /// <summary>The page as plain text.</summary>
    public string Report()
    {
        var text = new StringBuilder();
        text.AppendLine("Helm diagnostics");
        foreach (var f in Facts) text.AppendLine($"{f.Name}: {f.Value}");
        text.AppendLine(Summary);
        text.AppendLine();
        text.AppendLine("Shortcut conflicts:");
        if (Conflicts.Count == 0) text.AppendLine("  none");
        foreach (var c in Conflicts) text.AppendLine("  " + c);
        text.AppendLine();
        text.AppendLine("Monitors:");
        foreach (var m in Monitors) text.AppendLine($"  {m.Name}  bounds {m.Bounds}  work area {m.WorkArea}  {m.Scale}");
        text.AppendLine();
        text.AppendLine("App windows:");
        foreach (var w in Windows) text.AppendLine($"  {w.Handle}  {w.Process}  {w.Bounds}  {w.Flags}  {w.Title}");
        return text.ToString();
    }

    partial void OnIsHookTestEnabledChanged(bool value)
    {
        if (!value)
        {
            Stop();
            HookStatus = Idle;
            return;
        }
        try
        {
            _watch = _source.WatchInput(
                key =>
                {
                    var n = Interlocked.Increment(ref _keys);
                    _ui.Post(() => LastKey = $"#{n} {key}");
                },
                mouse =>
                {
                    var n = Interlocked.Increment(ref _moves);
                    // Every tenth move only: the page does not need to redraw for each pixel.
                    if (mouse.StartsWith("Move", StringComparison.Ordinal) && n % 10 != 0) return;
                    _ui.Post(() => LastMouse = $"#{n} {mouse}");
                });
            HookStatus = "Watching: type or move the mouse anywhere.";
        }
        catch (InvalidOperationException ex)
        {
            Stop();
            HookStatus = ex.Message;
        }
    }

    /// <summary>Stops the hooks test (the page is closed).</summary>
    public void Stop()
    {
        _watch?.Dispose();
        _watch = null;
    }

    private static void Replace<T>(ObservableCollection<T> list, IEnumerable<T> items)
    {
        list.Clear();
        foreach (var item in items) list.Add(item);
    }
}
