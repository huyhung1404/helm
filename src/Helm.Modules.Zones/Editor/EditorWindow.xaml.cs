using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Helm.Core.Desktop;

namespace Helm.Modules.Zones.Editor;

/// <summary>
/// Borderless full-screen overlay for one monitor. Placement is done in physical pixels (the process is
/// per-monitor-v2 DPI aware) and re-applied after WPF reacts to a DPI change, so mixed-DPI setups line up.
/// Only placement and keyboard glue live here; everything else is in <see cref="EditorViewModel"/>.
/// </summary>
public partial class EditorWindow : Window
{
    private readonly EditorViewModel _viewModel;
    private readonly IWindowService _windows;

    public EditorWindow(EditorViewModel viewModel, IWindowService windows)
    {
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;
        InitializeComponent();

        SourceInitialized += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Activated += (_, _) =>
        {
            if (!_viewModel.IsEditing) _viewModel.Reload();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => PickerScroll.MaxHeight = Math.Max(200, ActualHeight * 0.6);
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var monitor = _viewModel.Monitor;
        _windows.SetWindowBounds(hwnd, monitor.Bounds);

        // Position the zone layer over the work area (DIPs relative to this window).
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;
        var wa = monitor.WorkArea;
        var b = monitor.Bounds;
        WorkAreaHost.Margin = new Thickness(
            (wa.Left - b.Left) / scale,
            (wa.Top - b.Top) / scale,
            (b.Right - wa.Right) / scale,
            (b.Bottom - wa.Bottom) / scale);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _viewModel.HandleEscape();
    }
}
