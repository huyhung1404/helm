using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Helm.Modules.Missions.Ui;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions on Windows. The data lives in Helm Sync and the pages work on <see cref="MissionsStore"/>; the only
/// background work is the daily step reminder (a tray notification) while the tool is on. The menu and Quick access
/// and Home → Utilities open its page, the shared <see cref="MissionsView"/>; its settings are a sheet inside it.
/// </summary>
public sealed class MissionsModule : HelmModuleBase, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    private readonly MissionReminderService _reminders;
    // Steps sent to Tracker and their tasks finish together while this lives.
    private readonly MissionTaskSync _tasks;
    private readonly IServiceProvider _services;
    private readonly ILogger<MissionsModule> _logger;
    private readonly Timer _timer;

    // IUserNotifications and IShellNavigation are resolved when a reminder is shown: both depend on the module list,
    // which contains this module.
    public MissionsModule(MissionReminderService reminders, MissionTaskSync tasks, IServiceProvider services, ILogger<MissionsModule> logger)
    {
        _reminders = reminders;
        _tasks = tasks;
        _services = services;
        _logger = logger;
        _timer = new Timer(_ => CheckReminder(), null, Timeout.Infinite, Timeout.Infinite);
        _reminders.Requested += (_, reminder) => Show(reminder);
        // A new hour or a re-enabled reminder should not wait for the next tick.
        _reminders.Settings.Changed += (_, _) => { if (IsEnabled) _timer.Change(TimeSpan.FromSeconds(2), CheckEvery); };
    }

    public override string Id => MissionsIds.ModuleId;
    public override string DisplayName => MissionsIds.DisplayName;
    public override string Description => MissionsIds.Description;
    public override ModuleGroup Group => ModuleGroup.Planning;
    public override Symbol Icon => Symbol.Flag;
    public override IImage IconImage => MissionsIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    /// <summary>The view model is resolved here, the first time the tab opens, not with the module.</summary>
    public override Control CreatePage()
    {
        var viewModel = _services.GetRequiredService<MissionsViewModel>();
        var host = new WindowsMissionsPageHost(viewModel);
        return host.Attach(new MissionsView(this, viewModel, host));
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        // First check shortly after start (Helm usually starts with Windows), then every few minutes.
        _timer.Change(TimeSpan.FromSeconds(40), CheckEvery);
        _tasks.Sync();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    private void CheckReminder()
    {
        try
        {
            if (_reminders.TakeDue() is { } reminder) Show(reminder);
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "Missions reminder check failed");
        }
    }

    private void Show(MissionReminder reminder)
    {
        try
        {
            _services.GetRequiredService<IUserNotifications>().Show(reminder.Title, reminder.Message,
                () => _services.GetRequiredService<IShellNavigation>().ShowTool(MissionsIds.ModuleId));
            _logger.LogInformation("Missions reminder shown: {Title}", reminder.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the Missions reminder");
        }
    }
}
