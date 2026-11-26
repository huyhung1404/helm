using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

public static class MissionsServices
{
    public static IServiceCollection AddMissionsModule(this IServiceCollection services) =>
        services
            .AddMissionsCore()
            .AddMissionLinks(navigation => navigation.ShowTool(MissionsIds.ModuleId))
            .AddHelmModule<MissionsModule, MissionsViewModel>();
}
