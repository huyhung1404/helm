using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions on Android. The data lives in Helm Sync; the module sets the hourly alarm behind the daily step reminder
/// while the tool is on, and keeps the home-screen widgets in step with the data. Back closes the celebration and the
/// new-mission or re-plan panel before leaving the page.
/// </summary>
public sealed class MissionsModule : AndroidModuleBase, IModuleContent, IBackHandler, IDisposable
{
    private readonly MissionsViewModel _viewModel;
    // Steps sent to Tracker and their tasks finish together while this lives.
    private readonly MissionTaskSync _tasks;
    private readonly ILogger<MissionsModule> _logger;
    private readonly Timer _widgetTimer;

    public MissionsModule(MissionsViewModel viewModel, MissionsStore store, MissionReminderService reminders, MissionTaskSync tasks, ILogger<MissionsModule> logger)
    {
        _viewModel = viewModel;
        _tasks = tasks;
        _logger = logger;
        // "Remind me now" on the settings page.
        reminders.Requested += (_, reminder) => MissionReminders.Post(AndroidApp.Context, reminder);
        _widgetTimer = new Timer(_ => RefreshWidgets(), null, Timeout.Infinite, Timeout.Infinite);
        // A sync can change many records in a row: redraw the widgets once it has settled.
        store.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _widgetTimer.Dispose();

    /// <summary>Whether the launcher can place the widget when asked (settings page button).</summary>
    public bool CanPinWidget => MissionsWidgets.CanRequestPin(AndroidApp.Context);

    /// <summary>Asks the launcher to add the Missions widget to the home screen.</summary>
    public void PinWidget() => MissionsWidgets.RequestPin(AndroidApp.Context);

    private void RefreshWidgets()
    {
        try
        {
            MissionsWidgets.RefreshAll(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "Could not refresh the Missions widgets");
        }
    }

    public override string Id => MissionsIds.ModuleId;
    public override string DisplayName => MissionsIds.DisplayName;
    public override string Description => MissionsIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override Symbol Icon => Symbol.Flag;

    /// <summary>The same vector icon as on Windows (<see cref="MissionsIconShape"/>).</summary>
    public override IImage? IconImage => MissionsIcon.Image;

    public override Type PageType => typeof(MissionsPage);
    public Type ContentPageType => typeof(MissionsContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        RefreshWidgets();
        _tasks.Sync();
        try
        {
            MissionReminders.Schedule(AndroidApp.Context);
            MissionReminders.CheckNow(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set the Missions reminder");
        }
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        // They show "Missions is turned off".
        RefreshWidgets();
        try
        {
            MissionReminders.Cancel(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the Missions reminder");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The content page is on screen: the moment to ask for notifications (Android 13+, once) and to catch today's
    /// reminder.
    /// </summary>
    public void PageShown()
    {
        var context = AndroidApp.Context;
        MissionReminders.AskPermissionOnce(context);
        MissionReminders.CheckNow(context);
    }

    /// <summary>Android back: the celebration, then an open panel, close before the page does.</summary>
    public bool HandleBack()
    {
        if (_viewModel.HasCelebration)
        {
            _viewModel.DismissCelebrationCommand.Execute(null);
            return true;
        }
        if (!_viewModel.IsNewOpen) return false;
        _viewModel.CloseNewCommand.Execute(null);
        return true;
    }
}
