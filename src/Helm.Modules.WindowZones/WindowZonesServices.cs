using Helm.Core;
using Helm.Modules.WindowZones.Editor;
using Helm.Modules.WindowZones.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.WindowZones;

public static class WindowZonesServices
{
    public static IServiceCollection AddWindowZonesModule(this IServiceCollection services) =>
        services
            .AddSingleton<WindowZonesData>()
            .AddSingleton<ZoneTabStrip>()
            .AddSingleton<ZonesEditorController>()
            .AddHelmModule<WindowZonesModule, WindowZonesPage, WindowZonesViewModel>()
            .AddSingleton<WindowZonesContentPage>();
}
