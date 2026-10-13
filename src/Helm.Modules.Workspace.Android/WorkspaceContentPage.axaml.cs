using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Workspace;

/// <summary>
/// The control screen, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the
/// tool is off on this phone it stays readable but nothing can be sent.
/// </summary>
public partial class WorkspaceContentPage : UserControl
{
    private readonly WorkspaceModule _module;
    private readonly WorkspaceRemoteViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public WorkspaceContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<WorkspaceModule>(), HelmAndroidServices.Current.GetRequiredService<WorkspaceRemoteViewModel>())
    {
    }

    public WorkspaceContentPage(WorkspaceModule module, WorkspaceRemoteViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Opened from a notification: straight to that conversation.
        if (Helm.Core.Platform.ActivityHost.Latest?.Intent is { } intent && intent.GetStringExtra(WorkspaceWatchService.ExtraSession) is { Length: > 0 } session)
        {
            var machine = intent.GetStringExtra(WorkspaceWatchService.ExtraMachine) ?? "";
            intent.RemoveExtra(WorkspaceWatchService.ExtraSession);
            intent.RemoveExtra(WorkspaceWatchService.ExtraMachine);
            _viewModel.OpenSession(machine, session);
        }
        _viewModel.PropertyChanged += OnViewModelChanged;
        ClearNotification();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceRemoteViewModel.SelectedSession)) ClearNotification();
    }

    /// <summary>The open conversation's notification is read now, but only while Helm is in front: the page stays
    /// attached in the background, where a refresh must not remove a notification nobody saw.</summary>
    private void ClearNotification()
    {
        if (Helm.Core.Platform.ActivityHost.Current is null) return;
        if (_viewModel.OpenSessionId is { } id) WorkspaceWatchService.Cancel(global::Android.App.Application.Context, id);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // Pages are transient: a new one is made each time the tool opens.
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        OffBar.IsVisible = !_module.IsEnabled;
        IsEnabled = true;
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => _viewModel.CloseSession();
}
