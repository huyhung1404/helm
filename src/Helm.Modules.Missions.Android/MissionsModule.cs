using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions on Android. The data lives in Helm Sync; the module sets the hourly alarm behind the daily step reminder
/// while the tool is on. Back closes the celebration and the new-mission or re-plan panel before leaving the page.
/// </summary>
public sealed class MissionsModule : AndroidModuleBase, IModuleContent, IBackHandler
{
    private readonly MissionsViewModel _viewModel;
    private readonly ILogger<MissionsModule> _logger;

    public MissionsModule(MissionsViewModel viewModel, MissionReminderService reminders, ILogger<MissionsModule> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        // "Remind me now" on the settings page.
        reminders.Requested += (_, reminder) => MissionReminders.Post(AndroidApp.Context, reminder);
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
