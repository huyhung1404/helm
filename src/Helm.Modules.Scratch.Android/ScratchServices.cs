using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Scratch;

public static class ScratchServices
{
    public static IServiceCollection AddScratchModule(this IServiceCollection services) =>
        services
            .AddSingleton<IScratchPlatform, AndroidScratchPlatform>()
            .AddScratchCore()
            .AddAndroidModule<ScratchModule, ScratchPage>()
            .AddTransient<ScratchContentPage>();
}
