using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Core;

public static class HelmCommonServices
{
    /// <summary>
    /// Registers what every Helm app shares: paths, settings and sync. The platform registers its
    /// <see cref="ISecretProtector"/> (and its module host) itself.
    /// </summary>
    public static IServiceCollection AddHelmCommon(this IServiceCollection services, HelmPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<ISettingsStoreFactory>(sp => new SettingsStoreFactory(paths, sp.GetService<ILoggerFactory>()));
        services.AddHelmSync(paths);
        return services;
    }
}
