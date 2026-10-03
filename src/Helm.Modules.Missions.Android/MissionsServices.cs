using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

public static class MissionsServices
{
    public static IServiceCollection AddMissionsModule(this IServiceCollection services) =>
        services
            .AddMissionsCore()
            .AddMissionLinks(typeof(MissionsContentPage))
            .AddAndroidModule<MissionsModule, MissionsPage>()
            .AddTransient<MissionsContentPage>();
}
