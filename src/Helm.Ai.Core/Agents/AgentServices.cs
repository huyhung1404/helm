using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Ai.Agents;

public static class AgentServices
{
    /// <summary>
    /// The proxy: the agents the user added to this machine (Codex, Claude Code, Grok, each signed in to its own account)
    /// take Helm's AI jobs first, for this machine's features and the phone's (through Helm Sync, <see cref="AgentJobWorker"/>);
    /// the API key only when none answers. Helm runs its own tools for them. The same agents serve the user's own VPS
    /// too, when set up (address and token are this machine's only, never synced). Helm on Windows and <c>helm serve</c>
    /// on Linux both call it, after <see cref="AiCoreServices.AddHelmAi"/>; the platform brings the
    /// <see cref="IChildProcessLauncher"/>, the <see cref="IDeviceInfo"/> and how a sign-in is shown.
    /// </summary>
    public static IServiceCollection AddAgentProxy(this IServiceCollection services, HelmPaths paths, Func<IServiceProvider, IAgentSignIn> signIn)
    {
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStoreFactory>();
            bool IsOn(string? moduleId) => moduleId is null
                || !settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules.TryGetValue(moduleId, out var on) || on;
            return new AgentRegistry(settings, paths, sp.GetRequiredService<IChildProcessLauncher>(), signIn(sp),
                sp.GetServices<IMcpToolProvider>(), IsOn, sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<Prompts.IAiPrompts>(), sp.GetRequiredService<AiKeyStore>());
        });
        services.AddSingleton<IAgentRoute>(sp => sp.GetRequiredService<AgentRegistry>());
        services.AddSingleton(sp =>
        {
            var engine = sp.GetRequiredService<SyncEngine>();
            var agents = sp.GetRequiredService<AgentRegistry>();
            return new AgentJobWorker(sp.GetRequiredService<ISyncedCollection<AiJobRecord>>(),
                sp.GetRequiredService<ISyncedCollection<AiWorkerRecord>>(), agents,
                () => engine.DeviceId, sp.GetRequiredService<IDeviceInfo>().DeviceName, agents.Ready,
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentJobWorker>(),
                photos: new BlobJobPhotos(sp.GetRequiredService<BlobStore>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<BlobJobPhotos>()));
        });
        services.AddSingleton(sp => new Vps.VpsWorkerToken(paths, sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton(sp =>
        {
            var agents = sp.GetRequiredService<AgentRegistry>();
            return new Vps.VpsWorker(() => new Vps.VpsWebSocket(), agents, agents.Ready, sp.GetRequiredService<IDeviceInfo>().DeviceName,
                AppInfo.Version, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Vps.VpsWorker>());
        });
        services.AddSingleton(sp => new Vps.VpsWorkerControl(
            sp.GetRequiredService<ISettingsStoreFactory>().Get<Vps.VpsWorkerSettings>(Vps.VpsWorkerSettings.StoreId),
            sp.GetRequiredService<Vps.VpsWorkerToken>(), sp.GetRequiredService<Vps.VpsWorker>()));
        return services;
    }
}
