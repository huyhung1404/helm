using Helm.Core.Modules;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.QuickCapture;
using Helm.Modules.Tracker;
using Helm.Modules.Vault;
using Helm.Modules.Wallet;
using Helm.Modules.WatchLater;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.Hosting;

/// <summary>
/// The one place where tools are wired into the Android app (the Windows app has its own list in
/// src/Helm.App/Hosting/HelmModules.cs). A tool that exists on both platforms is registered in both lists; a
/// PC-only tool such as Claude Chat appears only there. Add a line per tool, e.g. <c>services.AddMyToolModule();</c>,
/// where the tool's own project calls <see cref="AndroidModuleServices.AddAndroidModule{TModule, TPage}"/>.
/// </summary>
internal static class AndroidModules
{
    public static void Register(IServiceCollection services)
    {
        services.AddVaultModule();
        services.AddTrackerModule();
        services.AddMissionsModule();
        services.AddWalletModule();
        services.AddNotesModule();
        services.AddWatchLaterModule();
        services.AddQuickCaptureModule();
    }
}
