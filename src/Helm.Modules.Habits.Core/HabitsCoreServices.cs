using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Habits;

public static class HabitsIds
{
    /// <summary>Module id on both apps: the settings file (habits.json) and the enabled-state key.</summary>
    public const string ModuleId = "habits";

    public const string DisplayName = "Habits";

    public const string Description = "Build habits day by day: tick off each one when it is done, watch the year fill up in a grid, and write down why when you skip.";
}

public static class HabitsCoreServices
{
    /// <summary>
    /// The platform-neutral part of Habits: its three synced collections, the store, the reminder, the AI agents' tools
    /// and the page view model. The platform module (Windows or Android) calls this from its own <c>AddHabitsModule()</c>.
    /// </summary>
    public static IServiceCollection AddHabitsCore(this IServiceCollection services)
    {
        // Small records edited as a whole: the latest edit wins. A pull that deletes many habits or done days (a habit
        // deleted elsewhere) is held for a confirmation, so a mistake on one device cannot wipe a year of history.
        services.AddSyncedCollection(new SyncedCollectionOptions<Habit> { Name = HabitsStore.HabitsCollection, GuardDeletions = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<HabitCheck> { Name = HabitsStore.ChecksCollection, GuardDeletions = true });
        services.AddSyncedCollection(new SyncedCollectionOptions<HabitLapse> { Name = HabitsStore.LapsesCollection });
        services.AddSyncGroup("habits.", HabitsIds.DisplayName);
        services.AddSingleton(sp => new HabitsStore(
            sp.GetRequiredService<ISyncedCollection<Habit>>(),
            sp.GetRequiredService<ISyncedCollection<HabitCheck>>(),
            sp.GetRequiredService<ISyncedCollection<HabitLapse>>()));
        services.AddSingleton(sp => new HabitReminderService(sp.GetRequiredService<HabitsStore>(), sp.GetRequiredService<Helm.Core.Settings.ISettingsStoreFactory>()));
        // The AI agents' tools for the habits (Helm's MCP server, Windows).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider, HabitsMcpTools>();
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<HabitsViewModel>();
        return services;
    }
}
