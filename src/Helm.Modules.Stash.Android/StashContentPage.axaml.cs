using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Stash;

/// <summary>
/// Stash itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the list stays visible but read-only, with a note pointing to the settings.
/// </summary>
public partial class StashContentPage : UserControl
{
    private readonly StashModule _module;
    private readonly StashViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public StashContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<StashModule>(), HelmAndroidServices.Current.GetRequiredService<StashViewModel>())
    {
    }

    public StashContentPage(StashModule module, StashViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ApplyEnabled();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StashViewModel.IsTextBoxOpen) && _viewModel.IsTextBoxOpen)
            Dispatcher.UIThread.Post(() => TextBox.Focus(), DispatcherPriority.Background);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
