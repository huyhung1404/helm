using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the
/// tool is off the lists stay visible but read-only, with a note pointing to the settings.
/// </summary>
public partial class TrackerContentPage : UserControl
{
    private readonly TrackerModule _module;
    private readonly IShellNavigation _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public TrackerContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<TrackerModule>(), HelmAndroidServices.Current.GetRequiredService<TrackerViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel, IShellNavigation navigation)
    {
        _module = module;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        base.OnDetachedFromVisualTree(e);
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

    private void OpenSettings_Click(object? sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(TrackerPage));
}
