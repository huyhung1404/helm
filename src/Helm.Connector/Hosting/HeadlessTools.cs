using Helm.Core.Services;
using Helm.Modules.Habits;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Helm.Modules.Vocab;
using Helm.Modules.Wallet;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Connector.Hosting;

/// <summary>
/// The tools in the <see cref="SyncSelection"/>: each one's platform-neutral part, as the apps register it (its synced
/// collections, its store and its tools for agents), and its links (a note linked to a task). There are no pages: a
/// link is never opened here. Vault is never registered.
/// </summary>
internal static class HeadlessTools
{
    public static IServiceCollection Add(IServiceCollection services, SyncSelection selection)
    {
        services.AddSingleton<IShellNavigation, NoPages>();
        services.AddSingleton<IDialogService, NoPages>();
        var page = typeof(NoPages);
        if (selection.Has("tracker.")) services.AddTrackerCore().AddTrackerLinks(page);
        if (selection.Has("wallet.")) services.AddWalletCore().AddWalletLinks(page);
        if (selection.Has("habits.")) services.AddHabitsCore();
        if (selection.Has("notes.")) services.AddNotesCore().AddNoteLinks(page);
        if (selection.Has("missions.")) services.AddMissionsCore().AddMissionLinks(page);
        if (selection.Has("vocab.")) services.AddVocabCore();
        return services;
    }

    /// <summary>No screen: nothing to show, and no one to say yes to a question.</summary>
    private sealed class NoPages : IShellNavigation, IDialogService
    {
        public void ShowPage(Type pageType)
        {
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
    }
}
