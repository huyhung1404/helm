using Helm.Modules.CommandPalette;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.QuickCapture;
using Helm.Modules.Ssh;
using Helm.Modules.Scratch;
using Helm.Modules.Vault;
using Helm.Modules.Tracker;
using Helm.Modules.Wallet;
using Helm.Modules.WatchLater;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Hosting;

/// <summary>
/// The one place where modules are wired into the app. Add a line here for every new module, e.g.
/// <c>services.AddMyToolModule();</c> (see docs/development.md → Adding a module).
/// </summary>
internal static class HelmModules
{
    public static void Register(IServiceCollection services)
    {
        services.AddVaultModule();
        services.AddTrackerModule();
        services.AddMissionsModule();
        services.AddWalletModule();
        services.AddNotesModule();
        services.AddWatchLaterModule();
        services.AddScratchModule();
        services.AddSshModule();
        services.AddQuickCaptureModule();
        services.AddCommandPaletteModule();

        // Built but hidden; re-enable with (using Helm.Modules.AlwaysOnTop;):
        // services.AddAlwaysOnTopModule();
    }
}
