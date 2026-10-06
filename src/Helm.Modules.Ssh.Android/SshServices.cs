using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Ssh;

public static class SshServices
{
    public static IServiceCollection AddSshModule(this IServiceCollection services) =>
        services
            .AddSshCore()
            .AddAndroidModule<SshModule, SshPage>()
            .AddTransient<SshContentPage>();
}
