using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Stash;

public static class StashServices
{
    public static IServiceCollection AddStashModule(this IServiceCollection services) =>
        services
            .AddSingleton<IStashPlatform, WindowsStashPlatform>()
            .AddStashCore()
            .AddHelmModule<StashModule, StashPage, StashViewModel>()
            .AddSingleton<StashContentPage>();
}
