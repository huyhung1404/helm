using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker on Android. The module keeps the home-screen widgets in step with the data (local edits and synced
/// changes) and with the on/off state, and sets the hourly alarm behind the due-date reminders.
/// </summary>
public sealed class TrackerModule : AndroidModuleBase, IModuleContent, IDisposable
{
    private readonly ILogger<TrackerModule> _logger;
    private readonly Timer _widgetTimer;

    public TrackerModule(TrackerStore store, TrackerReminderService reminders, ILogger<TrackerModule> logger)
    {
        _logger = logger;
        // "Remind me now" on the settings page.
        reminders.Requested += (_, reminder) => TrackerReminders.Post(AndroidApp.Context, reminder);
        _widgetTimer = new Timer(_ => RefreshWidgets(), null, Timeout.Infinite, Timeout.Infinite);
        // A sync can change hundreds of records in a row: redraw the widgets once it has settled.
        store.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    public override string Id => TrackerIds.ModuleId;
    public override string DisplayName => TrackerIds.DisplayName;
    public override string Description => TrackerIds.Description;
    public override ModuleGroup Group => ModuleGroup.Planning;
    public override Symbol Icon => Symbol.TaskListSquare;

    /// <summary>The same vector icon as on Windows (<see cref="TrackerIconShape"/>).</summary>
    public override IImage? IconImage => TrackerIcon.Image;

    public override Type PageType => typeof(TrackerPage);
    public Type ContentPageType => typeof(TrackerContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        RefreshWidgets();
        TrackerReminders.Schedule(AndroidApp.Context);
        TrackerReminders.CheckNow(AndroidApp.Context);
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        RefreshWidgets(); // they show "Tracker is turned off"
        TrackerReminders.Cancel(AndroidApp.Context);
        return Task.CompletedTask;
    }

    public void Dispose() => _widgetTimer.Dispose();

    private void RefreshWidgets()
    {
        try
        {
            TrackerWidgets.RefreshAll(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "Could not refresh the Tracker widgets");
        }
    }
}
