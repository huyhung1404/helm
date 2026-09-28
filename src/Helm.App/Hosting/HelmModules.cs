using Helm.Modules.ClaudeChat;
using Helm.Modules.Vault;
using Helm.Modules.Tracker;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Hosting;

/// <summary>
/// The one place where modules are wired into the app. Add a line here for every new module, e.g.
/// <c>services.AddMyToolModule();</c> (see README → Adding a module).
/// </summary>
internal static class HelmModules
{
    public static void Register(IServiceCollection services)
    {
        services.AddClaudeChatModule();
        services.AddVaultModule();
        services.AddTrackerModule();

        // Built but hidden; re-enable with (using Helm.Modules.AlwaysOnTop;):
        // services.AddAlwaysOnTopModule();
    }
}
