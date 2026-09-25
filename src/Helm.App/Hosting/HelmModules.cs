using Helm.Modules.Zones;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Hosting;

/// <summary>The one place where modules are wired into the app. Add a line here for every new module.</summary>
internal static class HelmModules
{
    public static void Register(IServiceCollection services)
    {
        services.AddZonesModule();

        // Temporarily hidden; the module is still built and can be re-enabled with this one line:
        // services.AddAlwaysOnTopModule();   (using Helm.Modules.AlwaysOnTop;)
    }
}
