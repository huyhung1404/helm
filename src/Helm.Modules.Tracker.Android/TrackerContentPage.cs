using Avalonia.Controls;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Ui;
using Helm.Modules.Tracker.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the
/// shared <see cref="TrackerView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>), plus what
/// only Android does: sharing the calendar file, asking for notifications, the widget settings.
/// </summary>
public sealed class TrackerContentPage : UserControl, IFullPage, ITrackerPageHost
{
    private readonly TrackerViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public TrackerContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<TrackerModule>(), HelmAndroidServices.Current.GetRequiredService<TrackerViewModel>())
    {
    }

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel)
    {
        _viewModel = viewModel;
        Content = new TrackerView(module, viewModel, this);
    }

    /// <summary>The calendar as an .ics file through the share sheet (a calendar app, mail, Drive…).</summary>
    public void ExportCalendar(string ics)
    {
        try
        {
            var sharer = HelmAndroidServices.Current.GetRequiredService<IFileSharer>();
            Directory.CreateDirectory(sharer.ShareFolder);
            var path = Path.Combine(sharer.ShareFolder, $"helm-tracker-{DateTime.Now:yyyyMMdd}.ics");
            File.WriteAllText(path, ics, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sharer.ShareFile(path, "text/calendar", "Share the calendar");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }

    /// <summary>Opening Tracker is the moment to ask for notifications (Android 13+, once) and to catch today's reminder.</summary>
    public void Opened()
    {
        var context = Android.App.Application.Context;
        TrackerReminders.AskPermissionOnce(context);
        TrackerReminders.CheckNow(context);
    }

    public Control? CreatePlatformSettings() => new TrackerAndroidSettings(_viewModel);

    /// <summary>The page has no app bar of the shell's: its menu button opens the drawer.</summary>
    public Action<Control>? OpenMenu => ShellChrome.OpenNavigation;
}
