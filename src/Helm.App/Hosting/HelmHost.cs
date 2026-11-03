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
        // This PC's Claude Code as an agent for Helm's AI jobs (free on the user's plan): for this PC's own features, and
        // for the phone's, which reach it through Helm Sync (AgentJobWorker). Helm runs its own tools for it.
        builder.Services.AddSingleton<Ai.ClaudeCodeAgentFactory>();
        builder.Services.AddSingleton(sp =>
        {
            var agent = sp.GetRequiredService<Ai.ClaudeCodeAgentFactory>();
            var settings = sp.GetRequiredService<ISettingsStoreFactory>();
            bool IsOn(string? moduleId) => moduleId is null
                || !settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules.TryGetValue(moduleId, out var on) || on;
            return new Helm.Ai.Assistant.LocalAgentRoute(Environment.MachineName, Helm.Ai.AiProviders.Claude, agent,
                sp.GetServices<Core.Mcp.IMcpToolProvider>(), IsOn, agent.Unavailable);
        });
        builder.Services.AddSingleton<Helm.Ai.Assistant.IAgentRoute>(sp => sp.GetRequiredService<Helm.Ai.Assistant.LocalAgentRoute>());
        builder.Services.AddSingleton(sp =>
        {
            var engine = sp.GetRequiredService<Core.Sync.SyncEngine>();
            return new Helm.Ai.Assistant.AgentJobWorker(sp.GetRequiredService<Core.Sync.ISyncedCollection<Helm.Ai.Assistant.AiJobRecord>>(),
                sp.GetRequiredService<Core.Sync.ISyncedCollection<Helm.Ai.Assistant.AiWorkerRecord>>(), sp.GetRequiredService<Helm.Ai.Assistant.LocalAgentRoute>(),
                () => engine.DeviceId, sp.GetRequiredService<IDeviceInfo>().DeviceName, [Helm.Ai.AiProviders.Claude],
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<Helm.Ai.Assistant.AgentJobWorker>());
        });

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
        builder.Services.AddSingleton<AiViewModel>();
        builder.Services.AddSingleton<AiPage>();
        builder.Services.AddSingleton<WelcomePage>();
        builder.Services.AddSingleton<WhatsNewPage>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();
        builder.Services.AddSingleton<DiagnosticsPage>();

        return builder.Build();
    }
}
