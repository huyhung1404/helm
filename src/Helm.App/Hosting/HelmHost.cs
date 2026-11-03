using Helm.App.Services;
using Helm.App.ViewModels;
using Helm.App.Views;
using Helm.App.Views.Pages;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Helm.App.Hosting;

internal static class HelmHost
{
    public static IHost Build(HelmPaths paths, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(dispose: false);

        // Velopack-aware location first so Core's TryAdd fallback is skipped.
        builder.Services.AddSingleton<Core.Services.IAppLocation, Updates.VelopackAppLocation>();

        // Core infrastructure (settings, hooks, hotkeys, window/monitor services, module registry)
        builder.Services.AddHelmCore(paths);

        // The AI models Helm calls with the user's API keys (on Helm Sync).
        Helm.Ai.AiCoreServices.AddHelmAi(builder.Services);
        // The proxy: the agents the user added to this PC (Codex, Claude Code…, each signed in to its own account) take
        // Helm's AI jobs first, for this PC's features and the phone's (through Helm Sync, AgentJobWorker); the API key
        // only when none answers. Helm runs its own tools for them.
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStoreFactory>();
            bool IsOn(string? moduleId) => moduleId is null
                || !settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules.TryGetValue(moduleId, out var on) || on;
            return new Ai.AgentRegistry(settings, paths, sp.GetRequiredService<Core.Processes.IChildProcessLauncher>(),
                sp.GetServices<Core.Mcp.IMcpToolProvider>(), IsOn, sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<Helm.Ai.Prompts.IAiPrompts>());
        });
        builder.Services.AddSingleton<Helm.Ai.Assistant.IAgentRoute>(sp => sp.GetRequiredService<Ai.AgentRegistry>());
        builder.Services.AddSingleton(sp =>
        {
            var engine = sp.GetRequiredService<Core.Sync.SyncEngine>();
            var agents = sp.GetRequiredService<Ai.AgentRegistry>();
            return new Helm.Ai.Assistant.AgentJobWorker(sp.GetRequiredService<Core.Sync.ISyncedCollection<Helm.Ai.Assistant.AiJobRecord>>(),
                sp.GetRequiredService<Core.Sync.ISyncedCollection<Helm.Ai.Assistant.AiWorkerRecord>>(), agents,
                () => engine.DeviceId, sp.GetRequiredService<IDeviceInfo>().DeviceName, agents.Ready,
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<Helm.Ai.Assistant.AgentJobWorker>());
        });
        // The jobs this PC runs by itself on the same agents (the morning brief, the evening review…): Helm's own read
        // tools in-process, plus what Scratch holds (never on the MCP server); never while a Vault is unlocked.
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStoreFactory>();
            bool IsOn(string? moduleId) => moduleId is null
                || !settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules.TryGetValue(moduleId, out var on) || on;
            var engine = sp.GetRequiredService<Core.Sync.SyncEngine>();
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var tools = new Helm.Ai.Auto.AutoJobTools(
                [.. sp.GetServices<Core.Mcp.IMcpToolProvider>(), new Modules.Scratch.ScratchDigestTool(sp.GetRequiredService<Modules.Scratch.ScratchStore>())], IsOn);
            var asker = new Helm.Ai.Auto.AutoJobAsker(sp.GetRequiredService<Ai.AgentRegistry>(), sp.GetRequiredService<Helm.Ai.AiKeyStore>(),
                sp.GetServices<Helm.Ai.Assistant.IAssistantChatFactory>(), sp.GetRequiredService<Helm.Ai.AiUsageBook>(),
                loggers.CreateLogger<Helm.Ai.Auto.AutoJobAsker>());
            return new Helm.Ai.Auto.AutoJobScheduler(Helm.Ai.Auto.Jobs.AutoJobs.All(),
                sp.GetRequiredService<Core.Sync.ISyncedSettings<Helm.Ai.AiFeatureSettings>>(), sp.GetRequiredService<Helm.Ai.Auto.AutoJobRuns>(),
                sp.GetRequiredService<Core.Sync.ISyncedCollection<Helm.Ai.Assistant.AiWorkerRecord>>(), asker, tools,
                sp.GetRequiredService<Helm.Ai.Auto.AiBriefBook>(), () => engine.DeviceId, sp.GetRequiredService<IDeviceInfo>().DeviceName,
                () => sp.GetService<Core.Secrets.IVaultSecrets>()?.IsUnlocked == true, logger: loggers.CreateLogger<Helm.Ai.Auto.AutoJobScheduler>());
        });
        // The same agents serve the user's own VPS too, when set up on AI → Proxy (this PC only: address and token are
        // never synced).
        builder.Services.AddSingleton(sp => new Helm.Ai.Vps.VpsWorkerToken(paths, sp.GetRequiredService<Core.Sync.ISecretProtector>()));
        builder.Services.AddSingleton(sp =>
        {
            var agents = sp.GetRequiredService<Ai.AgentRegistry>();
            return new Helm.Ai.Vps.VpsWorker(() => new Helm.Ai.Vps.VpsWebSocket(), agents, agents.Ready, sp.GetRequiredService<IDeviceInfo>().DeviceName,
                Shell.Services.AppInfo.Version, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Helm.Ai.Vps.VpsWorker>());
        });
        builder.Services.AddSingleton(sp => new Helm.Ai.Vps.VpsWorkerControl(
            sp.GetRequiredService<ISettingsStoreFactory>().Get<Helm.Ai.Vps.VpsWorkerSettings>(Helm.Ai.Vps.VpsWorkerSettings.StoreId),
            sp.GetRequiredService<Helm.Ai.Vps.VpsWorkerToken>(), sp.GetRequiredService<Helm.Ai.Vps.VpsWorker>()));

        // Modules — each module assembly exposes one registration extension.
        HelmModules.Register(builder.Services);

        // Shell services
        builder.Services.AddSingleton<Core.Services.IUiDispatcher, UiDispatcher>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<TabManager>();
        builder.Services.AddSingleton<ShellNavigator>();
        builder.Services.AddSingleton<IShellNavigator>(sp => sp.GetRequiredService<ShellNavigator>());
        builder.Services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellNavigator>());
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IClipboardService, ClipboardService>();
        builder.Services.AddSingleton<IDeviceInfo, DeviceInfo>();
        builder.Services.AddSingleton<TrayService>();
        builder.Services.AddSingleton<IUserNotifications>(sp => sp.GetRequiredService<TrayService>());
        builder.Services.AddSingleton<SearchService>();
        builder.Services.AddSingleton<ShellController>();
        builder.Services.AddSingleton<Updates.VelopackUpdateService>();
        builder.Services.AddSingleton<Core.Services.IUpdateService>(sp => sp.GetRequiredService<Updates.VelopackUpdateService>());

        // Helm's MCP server: the tools of the modules that are on, for AI agents (Claude Code, VS Code, any MCP client).
        // Calls that change things ask first by Helm's policy (docs/mcp-security.md); the activity log stays on this device.
        builder.Services.AddSingleton<Mcp.McpConsentService>();
        builder.Services.AddSingleton(sp => new Core.Mcp.McpConsentPolicy(
            sp.GetRequiredService<Mcp.McpConsentService>(),
            new Core.Mcp.McpActivityLog(Core.Mcp.McpActivityLog.FileIn(paths)),
            () => sp.GetRequiredService<ISettingsStoreFactory>().Get<Core.Mcp.McpSettings>(Core.Mcp.McpSettings.StoreId).Current.AskBeforeChanges,
            () => Environment.IsPrivilegedProcess,
            notify: (title, message) => sp.GetRequiredService<IUserNotifications>().Show(title, message)));
        builder.Services.AddSingleton<Core.Mcp.IMcpConsent>(sp => sp.GetRequiredService<Core.Mcp.McpConsentPolicy>());
        builder.Services.AddSingleton(sp =>
        {
            var host = new Core.Mcp.McpPipeHost(paths, () =>
            {
                var settings = sp.GetRequiredService<ISettingsStoreFactory>().Get<Core.Mcp.McpSettings>(Core.Mcp.McpSettings.StoreId).Current;
                if (!settings.Enabled) return null;
                var modules = sp.GetRequiredService<Core.Modules.IModuleHost>();
                var tools = sp.GetServices<Core.Mcp.IMcpToolProvider>()
                    .Where(p => p.ModuleId is not { } id || modules.Find(id)?.IsEnabled == true)
                    .SelectMany(p => p.Tools)
                    .Where(t => settings.AllowChanges || t.ReadOnly);
                return new Core.Mcp.McpServer(tools, Shell.Services.AppInfo.Version, Core.Mcp.McpEndpoint.Instructions,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Core.Mcp.McpServer>(), sp.GetRequiredService<Core.Mcp.IMcpConsent>());
            }, sp.GetRequiredService<ILogger<Core.Mcp.McpPipeHost>>());
            // "Allow for this session" ends with the connection it was given to.
            var policy = sp.GetRequiredService<Core.Mcp.McpConsentPolicy>();
            host.ConnectionsChanged += (_, _) => policy.KeepConnections(host.Connections.Select(c => c.Id));
            return host;
        });

        // Shell views and view models
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TabsPage>();
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<UpdatesViewModel>();
        builder.Services.AddSingleton<SyncViewModel>();
        builder.Services.AddSingleton<BackgroundActivitiesViewModel>();
        builder.Services.AddSingleton<SyncIndicatorViewModel>();
        builder.Services.AddSingleton<GeneralViewModel>();
        builder.Services.AddSingleton<GeneralPage>();
        builder.Services.AddSingleton<McpPermissionsViewModel>();
        builder.Services.AddSingleton<McpViewModel>();
        builder.Services.AddSingleton<Helm.Ai.AiKeysViewModel>();
        builder.Services.AddSingleton<VpsWorkerViewModel>();
        builder.Services.AddSingleton<AiProxyViewModel>();
        builder.Services.AddSingleton<AiViewModel>();
        builder.Services.AddSingleton<AiPage>();
        builder.Services.AddSingleton<WelcomePage>();
        builder.Services.AddSingleton<WhatsNewPage>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();
        builder.Services.AddSingleton<DiagnosticsPage>();

        return builder.Build();
    }
}
