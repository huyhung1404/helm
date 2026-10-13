using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.Workspace;

/// <summary>
/// The management screen, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>): this PC
/// as a host and the VS Code windows connected to it, with their Claude sessions. Read-only: the phone steers.
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
}
