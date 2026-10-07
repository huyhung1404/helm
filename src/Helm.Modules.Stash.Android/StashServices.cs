using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Stash;

public static class StashServices
{
    public static IServiceCollection AddStashModule(this IServiceCollection services) =>
        services
            .AddSingleton<IStashPlatform, AndroidStashPlatform>()
            .AddStashCore()
            .AddAndroidModule<StashModule, StashPage>()
            .AddTransient<StashContentPage>();
}
