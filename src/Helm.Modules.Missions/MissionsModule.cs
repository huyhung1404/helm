using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions on Windows. The data lives in Helm Sync and the pages work on <see cref="MissionsStore"/>; the only
/// background work is the daily step reminder (a tray notification) while the tool is on. The menu and Quick access
/// open <see cref="MissionsContentPage"/>; Home → Utilities opens the settings (<see cref="MissionsPage"/>).
/// </summary>
public sealed class MissionsModule : HelmModuleBase, IModuleContent, IDisposable
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
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override SymbolRegular Icon => SymbolRegular.Flag24;
    public override ImageSource IconImage => MissionsLogo.Image;
    public override Type SettingsPageType => typeof(MissionsPage);
    public Type ContentPageType => typeof(MissionsContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

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
                () => _services.GetRequiredService<IShellNavigation>().ShowPage(typeof(MissionsContentPage)));
            _logger.LogInformation("Missions reminder shown: {Title}", reminder.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the Missions reminder");
        }
    }
}
