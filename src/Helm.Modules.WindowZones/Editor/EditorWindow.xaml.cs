using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Desktop;

namespace Helm.Modules.WindowZones.Editor;

/// <summary>Per-window view model: the shared session plus whether this is the monitor that shows the toolbar.</summary>
public sealed class EditorWindowViewModel(EditorSession session, MonitorInfo monitor, bool isHome) : ObservableObject
{
    public EditorSession Session { get; } = session;
    public MonitorInfo Monitor { get; } = monitor;
    public bool IsHome { get; } = isHome;
    public string MonitorCaption => $"{Monitor.DisplayName}: draw zones here too. The toolbar is on {Session.Home.DisplayName}.";
}

/// <summary>
/// Borderless full-screen editor for one monitor. Placement is done in physical pixels (the process is
/// per-monitor-v2 DPI aware) and re-applied after WPF reacts to a DPI change. Only placement and keyboard glue
/// live here; everything else is in <see cref="EditorSession"/>.
/// </summary>
public partial class EditorWindow : Window
{
    private readonly EditorWindowViewModel _viewModel;
    private readonly IWindowService _windows;

    public EditorWindow(EditorWindowViewModel viewModel, IWindowService windows, Color accent)
    {
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;
        InitializeComponent();
        // A dark see-through veil over the desktop, the same in both themes, so the zones stand out.
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10));
        CanvasHost.Content = new ZoneCanvas(viewModel.Session, viewModel.Monitor, accent);

        SourceInitialized += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) _windows.SetWindowBounds(hwnd, _viewModel.Monitor.Bounds);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _viewModel.Session.HandleEscape();
                break;
            case Key.Delete when Keyboard.FocusedElement is not System.Windows.Controls.TextBox:
                e.Handled = true;
                _viewModel.Session.HandleDelete();
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                _viewModel.Session.SaveCommand.Execute(null);
                break;
        }
    }
}
