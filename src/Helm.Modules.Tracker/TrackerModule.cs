using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Helm.Modules.Tracker.Ui;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker on Windows. The data lives in Helm Sync and the pages work directly on <see cref="TrackerStore"/>; the only
/// background work is the daily due-date reminder (a tray notification) while the tool is on. The menu and Quick
/// access open its page, the shared <see cref="TrackerView"/>, and so does Home → Utilities: its settings are a sheet inside that page
/// (the gear), so there is no separate settings page.
/// </summary>
public sealed class TrackerModule : HelmModuleBase, IDisposable
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
    public override ModuleGroup Group => ModuleGroup.Planning;
    public override Symbol Icon => Symbol.TaskListSquare;
    public override IImage IconImage => TrackerIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    /// <summary>The view model is resolved here, the first time the tab opens, not with the module.</summary>
    public override Control CreatePage()
    {
        var viewModel = _services.GetRequiredService<TrackerViewModel>();
        var host = new WindowsTrackerPageHost(viewModel);
        return host.Attach(new TrackerView(this, viewModel, host));
    }

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
                () => _services.GetRequiredService<IShellNavigation>().ShowTool(TrackerIds.ModuleId));
            _logger.LogInformation("Tracker reminder shown: {Title}", reminder.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the Tracker reminder");
        }
    }
}
