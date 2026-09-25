using Helm.Core.Desktop;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Zones.Editor;

/// <summary>Opens one <see cref="EditorWindow"/> per monitor on the UI thread; any of them closing closes all.</summary>
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
    public void Toggle(int pickerColumns) => ui.Post(() =>
    {
        if (IsOpen) Close();
        else Open(pickerColumns);
    });

    public void Show(int pickerColumns) => ui.Post(() =>
    {
        if (!IsOpen) Open(pickerColumns);
    });

    public void Close()
    {
        foreach (var w in _open.ToList()) w.Close();
        _open.Clear();
    }

    private void Open(int pickerColumns)
    {
        try
        {
            var cursorMonitor = monitors.FromPoint(windows.GetCursorPosition());
            EditorWindow? focus = null;
            foreach (var monitor in monitors.GetMonitors())
            {
                var vm = new EditorViewModel(monitor, data, pickerColumns);
                vm.CloseRequested += (_, _) => ui.Post(Close);
                var window = new EditorWindow(vm, windows);
                window.Closed += (_, _) => _open.Remove(window);
                _open.Add(window);
                window.Show();
                if (monitor.Id == cursorMonitor?.Id) focus = window;
            }
            (focus ?? _open.FirstOrDefault())?.Activate();
            logger.LogInformation("Zones editor opened on {Count} monitor(s)", _open.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open the Zones editor");
            Close();
        }
    }
}
