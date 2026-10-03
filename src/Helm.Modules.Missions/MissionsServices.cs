using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

public static class MissionsServices
{
    public static IServiceCollection AddMissionsModule(this IServiceCollection services) =>
        services
            .AddMissionsCore()
            .AddHelmModule<MissionsModule, MissionsPage, MissionsViewModel>()
            .AddSingleton<MissionsContentPage>();
}
