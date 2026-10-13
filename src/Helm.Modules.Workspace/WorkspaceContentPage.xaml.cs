using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Core.Modules;

namespace Helm.Modules.Workspace;

/// <summary>
/// The sessions, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>): VS Code windows and
/// their Claude sessions on the left, the chosen session's last answer and a composer on the right.
/// </summary>
public partial class WorkspaceContentPage : Page
{
    private readonly WorkspaceModule _module;

    public WorkspaceContentPage(WorkspaceModule module, WorkspaceViewModel viewModel)
    {
        _module = module;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscription lives as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>View glue: a list inside the scrolling column passes the wheel on instead of swallowing it.</summary>
    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject list) return;
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(list) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = list });
    }
}
