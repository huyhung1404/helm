using Helm.Core.Services;
using Helm.Modules.Habits;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Helm.Modules.Vocab;
using Helm.Modules.Wallet;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Cli.Hosting;

/// <summary>
/// The tools whose data the phone's agent jobs read and change here: each tool's platform-neutral part, as the apps
/// register it (its synced collections, its store and its tools for the agents), and its links (a note linked to a
/// task). There are no pages: a link is never opened here.
/// </summary>
internal static class HeadlessTools
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddSingleton<IShellNavigation, NoPages>();
        services.AddSingleton<IDialogService, NoPages>();
        var page = typeof(NoPages);
        services.AddTrackerCore().AddTrackerLinks(page);
        services.AddWalletCore().AddWalletLinks(page);
        services.AddHabitsCore();
        services.AddNotesCore().AddNoteLinks(page);
        services.AddMissionsCore().AddMissionLinks(page);
        services.AddVocabCore();
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
