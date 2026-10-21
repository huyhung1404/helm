using System.Windows.Media;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WindowZones.Editor;

/// <summary>
/// Opens one <see cref="EditorWindow"/> per monitor on the UI thread, all sharing one <see cref="EditorSession"/>.
/// The monitor under the cursor shows the toolbar; closing one closes all.
/// </summary>
public sealed class ZonesEditorController(
    WindowZonesData data,
    IMonitorService monitors,
    IWindowService windows,
    IUiDispatcher ui,
    ILogger<ZonesEditorController> logger)
{
    private readonly List<EditorWindow> _open = [];

    public bool IsOpen => _open.Count > 0;

    /// <summary>Callable from any thread (e.g. the hotkey thread).</summary>
    public void Toggle() => ui.Post(() =>
    {
        if (IsOpen) Close();
        else Open();
    });

    public void Show() => ui.Post(() =>
    {
        if (!IsOpen) Open();
    });

    /// <summary>UI thread.</summary>
    public void Close()
    {
        foreach (var w in _open.ToList()) w.Close();
        _open.Clear();
    }

    private void Open()
    {
        try
        {
            var all = monitors.GetMonitors();
            if (all.Count == 0) return;
            var home = monitors.FromPoint(windows.GetCursorPosition()) is { } m ? all.FirstOrDefault(x => x.Id == m.Id) ?? all[0] : all[0];
            var accent = AccentColor.Current;
            var accentColor = Color.FromRgb(accent.R, accent.G, accent.B);

            var session = new EditorSession(data, all, home);
            session.CloseRequested += (_, _) => ui.Post(Close);

            EditorWindow? focus = null;
            foreach (var monitor in all)
            {
                var vm = new EditorWindowViewModel(session, monitor, monitor.Id == home.Id);
                var window = new EditorWindow(vm, windows, accentColor);
                window.Closed += (_, _) => _open.Remove(window);
                _open.Add(window);
                window.Show();
                if (monitor.Id == home.Id) focus = window;
            }
            (focus ?? _open.FirstOrDefault())?.Activate();
            logger.LogInformation("Zone editor opened on {Count} monitor(s), toolbar on {Home}", _open.Count, home.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open the zone editor");
            Close();
        }
    }
}
