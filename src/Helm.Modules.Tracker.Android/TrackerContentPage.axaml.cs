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
    private readonly TrackerViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public TrackerContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<TrackerModule>(), HelmAndroidServices.Current.GetRequiredService<TrackerViewModel>())
    {
    }

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The calendar as an .ics file through the share sheet (a calendar app, mail, Drive…).</summary>
    private void ShareIcs_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sharer = HelmAndroidServices.Current.GetRequiredService<IFileSharer>();
            Directory.CreateDirectory(sharer.ShareFolder);
            var path = Path.Combine(sharer.ShareFolder, $"helm-tracker-{DateTime.Now:yyyyMMdd}.ics");
            File.WriteAllText(path, _viewModel.CalendarIcs(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sharer.ShareFile(path, "text/calendar", "Share the calendar");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
        // Opening Tracker is the moment to ask for notifications (Android 13+, once) and to catch today's reminder.
        var context = Android.App.Application.Context;
        TrackerReminders.AskPermissionOnce(context);
        TrackerReminders.CheckNow(context);
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
}
