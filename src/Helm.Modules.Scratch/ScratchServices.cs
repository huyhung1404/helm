using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Scratch;

public static class ScratchServices
{
    public static IServiceCollection AddScratchModule(this IServiceCollection services) =>
        services
            .AddSingleton<IScratchPlatform, WindowsScratchPlatform>()
            .AddScratchCore()
            .AddHelmModule<ScratchModule, ScratchPage, ScratchViewModel>()
            .AddSingleton<ScratchContentPage>();
}
