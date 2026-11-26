using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

public static class MissionsServices
{
    public static IServiceCollection AddMissionsModule(this IServiceCollection services) =>
        services
            .AddMissionsCore()
            .AddMissionLinks(typeof(MissionsContentPage))
            .AddHelmModule<MissionsModule, MissionsContentPage, MissionsViewModel>()
            .AddSingleton<MissionsContentPage>();
}
