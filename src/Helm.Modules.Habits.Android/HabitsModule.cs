using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits on Android. The data lives in Helm Sync; the module sets the hourly alarm behind the skipped-habit reminder
/// while the tool is on. Back closes the page's sheet (a day being marked, the settings) before leaving the page, and
/// never leaves while a reason for skipped days is being asked for.
/// </summary>
public sealed class HabitsModule : AndroidModuleBase, IModuleContent, IBackHandler
{
    private readonly ILogger<HabitsModule> _logger;

    public HabitsModule(HabitReminderService reminders, ILogger<HabitsModule> logger)
    {
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
    public override IImage? IconImage => Ui.HabitsIcon.Image;

    public override Type PageType => typeof(HabitsContentPage);
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
    /// The content page is on screen (opened, or the app came back to it; the page asks for the reasons of skipped
    /// days itself): the moment to ask for notifications (Android 13+, once) and to catch today's reminder.
    /// </summary>
    public void PageShown()
    {
        var context = AndroidApp.Context;
        HabitReminders.AskPermissionOnce(context);
        HabitReminders.CheckNow(context);
    }

    /// <summary>
    /// Android back: the sheet on top closes first (closing a day being marked dismisses it); a reason still to write
    /// for skipped days keeps its sheet, and the page.
    /// </summary>
    public bool HandleBack() => Helm.Core.Ui.Sheet.CloseTopmost();
}
