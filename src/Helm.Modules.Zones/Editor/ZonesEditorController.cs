using System.Windows.Media;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Zones.Editor;

/// <summary>
/// Opens one <see cref="EditorWindow"/> per monitor on the UI thread, all sharing one <see cref="EditorSession"/>
/// (so a zone can be drawn across monitors). The monitor under the cursor shows the picker; closing closes all.
/// </summary>
public sealed class ZonesEditorController(
    ZonesDataService data,
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
            var accentColor = ToColor(AccentColor.Current);
            var accentBrush = new SolidColorBrush(accentColor);
            accentBrush.Freeze();

            var session = new EditorSession(data, all, home);
            session.CloseRequested += (_, _) => ui.Post(Close);

            EditorWindow? focus = null;
            foreach (var monitor in all)
            {
                var vm = new EditorWindowViewModel(session, monitor, monitor.Id == home.Id, accentBrush);
                var window = new EditorWindow(vm, windows, accentColor);
                window.Closed += (_, _) => _open.Remove(window);
                _open.Add(window);
                window.Show();
                if (monitor.Id == home.Id) focus = window;
            }
            (focus ?? _open.FirstOrDefault())?.Activate();
            logger.LogInformation("Zones editor opened on {Count} monitor(s), picker on {Home}", _open.Count, home.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open the Zones editor");
            Close();
        }
    }

    private static Color ToColor(Helm.Core.Geometry.RgbColor c) => Color.FromRgb(c.R, c.G, c.B);
}
