using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Ssh;

public static class SshServices
{
    public static IServiceCollection AddSshModule(this IServiceCollection services) =>
        services
            .AddSshCore()
            .AddHelmModule<SshModule, SshPage, SshViewModel>()
            .AddSingleton<SshContentPage>();
}
