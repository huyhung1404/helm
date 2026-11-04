using Helm.Core;
using Helm.Core.Processes;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Cli.Hosting;

/// <summary>
/// helm's services: Helm.Core's settings and sync and Helm.Ai.Core's keys and assistant, exactly as the apps register
/// them, on this box's host key instead of DPAPI or the Android Keystore. A command adds what it needs on top
/// (<c>helm serve</c>: the agents and the tools' stores).
/// </summary>
internal static class CliHost
{
    public static ServiceProvider Build(HelmPaths paths, ISecretProtector secrets, ILoggerFactory loggers, Action<IServiceCollection>? add = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton(secrets);
        services.AddHelmCommon(paths);
        Helm.Ai.AiCoreServices.AddHelmAi(services);

        services.AddSingleton<IChildProcessLauncher, PlainChildProcessLauncher>();
        services.AddSingleton<IDeviceInfo, MachineDeviceInfo>();
        services.AddSingleton<IUiDispatcher, HeadlessUiDispatcher>();
        add?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
