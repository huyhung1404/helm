using Helm.Core;
using Helm.Modules.Zones.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Zones;

public static class ZonesServices
{
    public static IServiceCollection AddZonesModule(this IServiceCollection services)
    {
        services.AddSingleton<ZonesDataService>();
        services.AddSingleton<ZonesEditorController>();
        return services.AddHelmModule<ZonesModule, ZonesPage, ZonesViewModel>();
    }
}
