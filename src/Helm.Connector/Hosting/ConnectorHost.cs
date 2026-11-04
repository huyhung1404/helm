using Helm.Connector.Sync;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Hosting;

/// <summary>
/// The connector's services: Helm.Core's settings and sync and the stores and agent tools of the tools in the
/// <see cref="SyncSelection"/>, registered exactly as the apps do, on this box's host key instead of DPAPI or the
/// Android Keystore, and on the HTTPS transport behind a <see cref="PushBreaker"/>. No agents: Fairy Core runs those.
/// </summary>
internal static class ConnectorHost
{
    public static ServiceProvider Build(HelmPaths paths, ISecretProtector secrets, ILoggerFactory loggers, SyncSelection selection,
        TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton(secrets);
        // Before AddHelmCommon, whose transport registrations only fill what is not there yet.
        services.AddSingleton(sp => new PushBreaker(
            new HttpSyncTransport(sp.GetRequiredService<ISyncCredentialStore>(), sp.GetRequiredService<SyncApiClient>()), time));
        services.AddSingleton<ISyncTransport>(sp => sp.GetRequiredService<PushBreaker>());
        services.AddHelmCommon(paths);
        // AI's synced collections and stores (keys, briefs, the features' settings) and Ask Helm's tools on them.
        Helm.Ai.AiCoreServices.AddHelmAi(services);

        services.AddSingleton<IDeviceInfo, MachineDeviceInfo>();
        services.AddSingleton<IUiDispatcher, HeadlessUiDispatcher>();
        HeadlessTools.Add(services, selection);
        return services.BuildServiceProvider();
    }
}
