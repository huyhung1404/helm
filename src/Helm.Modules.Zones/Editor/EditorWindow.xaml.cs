using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Desktop;

namespace Helm.Modules.Zones.Editor;

/// <summary>Per-window view model: the shared session plus whether this is the monitor that shows the picker.</summary>
public sealed partial class EditorWindowViewModel : ObservableObject
{
    public EditorWindowViewModel(EditorSession session, MonitorInfo monitor, bool isHome, Brush accent)
    {
        Session = session;
        Monitor = monitor;
        IsHome = isHome;
        AccentBrush = accent;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorSession.IsEditing))
            {
                OnPropertyChanged(nameof(ShowPicker));
                OnPropertyChanged(nameof(ShowToolbar));
                OnPropertyChanged(nameof(MonitorCaption));
            }
        };
    }

    public EditorSession Session { get; }
    public MonitorInfo Monitor { get; }
    public bool IsHome { get; }
    public Brush AccentBrush { get; }
    public bool ShowPicker => IsHome && !Session.IsEditing;
    public bool ShowToolbar => IsHome && Session.IsEditing;
    public string MonitorCaption => Session.IsEditing
        ? $"{Monitor.DisplayName} — draw here too; zones may cross monitors in an \"across monitors\" layout"
        : $"{Monitor.DisplayName} — the layout picker is on {Session.Home.DisplayName}";
}

/// <summary>
/// Borderless full-screen overlay for one monitor. Placement is done in physical pixels (the process is
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
        CanvasHost.Content = new ZoneCanvas(viewModel.Session, viewModel.Monitor, accent);

        SourceInitialized += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => PickerScroll.MaxHeight = Math.Max(200, ActualHeight * 0.55);
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
            case Key.Delete when _viewModel.Session.IsEditing && Keyboard.FocusedElement is not System.Windows.Controls.TextBox:
                e.Handled = true;
                _viewModel.Session.HandleDelete();
                break;
        }
    }
}
