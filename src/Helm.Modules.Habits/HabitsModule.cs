using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Helm.Modules.Habits.Ui;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits on Windows. The data lives in Helm Sync and the pages work on <see cref="HabitsStore"/>; the only background
/// work is the daily reminders (skipped habits, streaks at risk in the evening) as tray notifications while the tool is on. The menu and Quick
/// access and Home → Utilities open its page, the shared <see cref="HabitsView"/>; its settings are a sheet inside it.
/// </summary>
public sealed class HabitsModule : HelmModuleBase, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    private readonly HabitReminderService _reminders;
    private readonly IServiceProvider _services;
    private readonly ILogger<HabitsModule> _logger;
    private readonly Timer _timer;

    // IUserNotifications and IShellNavigation are resolved when a reminder is shown: both depend on the module list,
    // which contains this module.
    public HabitsModule(HabitReminderService reminders, IServiceProvider services, ILogger<HabitsModule> logger)
    {
        _reminders = reminders;
        _services = services;
        _logger = logger;
        _timer = new Timer(_ => CheckReminder(), null, Timeout.Infinite, Timeout.Infinite);
        _reminders.Requested += (_, reminder) => Show(reminder);
        // A new hour or a re-enabled reminder should not wait for the next tick.
        _reminders.Settings.Changed += (_, _) => { if (IsEnabled) _timer.Change(TimeSpan.FromSeconds(2), CheckEvery); };
    }

    public override string Id => HabitsIds.ModuleId;
    public override string DisplayName => HabitsIds.DisplayName;
    public override string Description => HabitsIds.Description;
    public override ModuleGroup Group => ModuleGroup.Planning;
    public override Symbol Icon => Symbol.CalendarCheckmark;
    public override IImage IconImage => HabitsIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    /// <summary>The view model is resolved here, the first time the tab opens, not with the module.</summary>
    public override Control CreatePage()
    {
        var host = new WindowsHabitsPageHost();
        return host.Attach(new HabitsView(this, _services.GetRequiredService<HabitsViewModel>(), host));
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        // First check shortly after start (Helm usually starts with Windows), then every few minutes.
        _timer.Change(TimeSpan.FromSeconds(45), CheckEvery);
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
            _logger.LogWarning(ex, "Habits reminder check failed");
        }
    }

    private void Show(HabitReminder reminder)
    {
        try
        {
            _services.GetRequiredService<IUserNotifications>().Show(reminder.Title, reminder.Message,
                () => _services.GetRequiredService<IShellNavigation>().ShowTool(HabitsIds.ModuleId));
            _logger.LogInformation("Habits reminder shown: {Title}", reminder.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the Habits reminder");
        }
    }
}
