using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Tracker.Ui;

/// <summary>What each platform does its own way around the shared Tracker page.</summary>
public interface ITrackerPageHost
{
    /// <summary>Hand the calendar file to the user: a save dialog on desktop, the share sheet on Android.</summary>
    void ExportCalendar(string ics);

    /// <summary>The page came on screen (Android asks for notifications here and checks today's reminder).</summary>
    void Opened();
}

/// <summary>
/// The Tracker itself, the same view on Windows, Android, macOS and iOS. While the tool is off the lists stay visible
/// but read-only, with a note pointing to the settings.
/// </summary>
public partial class TrackerView : UserControl
{
    private readonly IModule? _module;
    private readonly TrackerViewModel? _viewModel;
    private readonly ITrackerPageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public TrackerView() => InitializeComponent();

    public TrackerView(IModule module, TrackerViewModel viewModel, ITrackerPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void ShareIcs_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _host is null) return;
        _host.ExportCalendar(_viewModel.CalendarIcs());
    }

    // The module outlives the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null) return;
        _module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
        _host?.Opened();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
