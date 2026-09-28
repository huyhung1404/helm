using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker on Windows. The data lives in Helm Sync and the pages work directly on <see cref="TrackerStore"/>; the only
/// background work is the daily due-date reminder (a tray notification) while the tool is on. The menu and Quick
/// access open <see cref="TrackerContentPage"/>; Home → Utilities opens the settings (<see cref="TrackerPage"/>).
/// </summary>
public sealed class TrackerModule : HelmModuleBase, IModuleContent, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    private readonly TrackerReminderService _reminders;
    private readonly IServiceProvider _services;
    private readonly ILogger<TrackerModule> _logger;
    private readonly Timer _timer;

    // IUserNotifications and IShellNavigation are resolved when a reminder is shown: both depend on the module list,
    // which contains this module.
    public TrackerModule(TrackerReminderService reminders, IServiceProvider services, ILogger<TrackerModule> logger)
    {
        _reminders = reminders;
        _services = services;
        _logger = logger;
        _timer = new Timer(_ => CheckReminder(), null, Timeout.Infinite, Timeout.Infinite);
        _reminders.Requested += (_, reminder) => Show(reminder);
        // A new hour or a re-enabled reminder should not wait for the next tick.
        _reminders.Settings.Changed += (_, _) => { if (IsEnabled) _timer.Change(TimeSpan.FromSeconds(2), CheckEvery); };
    }

    public override string Id => TrackerIds.ModuleId;
    public override string DisplayName => TrackerIds.DisplayName;
    public override string Description => TrackerIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override SymbolRegular Icon => SymbolRegular.TaskListSquareLtr24;
    public override ImageSource IconImage => TrackerLogo.Image;
    public override Type SettingsPageType => typeof(TrackerPage);
    public Type ContentPageType => typeof(TrackerContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        // First check shortly after start (Helm usually starts with Windows), then every few minutes.
        _timer.Change(TimeSpan.FromSeconds(30), CheckEvery);
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
            _logger.LogWarning(ex, "Tracker reminder check failed");
        }
    }

    private void Show(TrackerReminder reminder)
    {
        try
        {
            _services.GetRequiredService<IUserNotifications>().Show(reminder.Title, reminder.Message,
                () => _services.GetRequiredService<IShellNavigation>().ShowPage(typeof(TrackerContentPage)));
            _logger.LogInformation("Tracker reminder shown: {Title}", reminder.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the Tracker reminder");
        }
    }
}
