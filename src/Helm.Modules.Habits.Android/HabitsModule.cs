using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits on Android. The data lives in Helm Sync; the module sets the hourly alarm behind the skipped-habit reminder
/// while the tool is on. Back closes the celebration before leaving the page, and never leaves while a reason for
/// skipped days is being asked for.
/// </summary>
public sealed class HabitsModule : AndroidModuleBase, IModuleContent, IBackHandler
{
    private readonly HabitsViewModel _viewModel;
    private readonly ILogger<HabitsModule> _logger;

    public HabitsModule(HabitsViewModel viewModel, HabitReminderService reminders, ILogger<HabitsModule> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        // "Remind me now" on the settings page.
        reminders.Requested += (_, reminder) => HabitReminders.Post(AndroidApp.Context, reminder);
    }

    public override string Id => HabitsIds.ModuleId;
    public override string DisplayName => HabitsIds.DisplayName;
    public override string Description => HabitsIds.Description;
    public override ModuleGroup Group => ModuleGroup.Planning;
    public override Symbol Icon => Symbol.CalendarCheckmark;

    /// <summary>The same vector icon as on Windows (<see cref="HabitsIconShape"/>).</summary>
    public override IImage? IconImage => HabitsIcon.Image;

    public override Type PageType => typeof(HabitsPage);
    public Type ContentPageType => typeof(HabitsContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        try
        {
            HabitReminders.Schedule(AndroidApp.Context);
            HabitReminders.CheckNow(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set the Habits reminder");
        }
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        try
        {
            HabitReminders.Cancel(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the Habits reminder");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The content page is on screen (opened, or the app came back to it): the moment to ask for the reasons of
    /// skipped days, to ask for notifications (Android 13+, once) and to catch today's reminder.
    /// </summary>
    public void PageShown()
    {
        _viewModel.Refresh();
        _viewModel.CheckLapses();
        var context = AndroidApp.Context;
        HabitReminders.AskPermissionOnce(context);
        HabitReminders.CheckNow(context);
    }

    /// <summary>Android back: the celebration closes first; a reason still to write keeps the page.</summary>
    public bool HandleBack()
    {
        if (_viewModel.HasPendingLapse && IsEnabled) return true;
        if (!_viewModel.HasCelebration) return false;
        _viewModel.DismissCelebrationCommand.Execute(null);
        return true;
    }
}
