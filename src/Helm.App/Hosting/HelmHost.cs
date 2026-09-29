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

        // Modules — each module assembly exposes one registration extension.
        HelmModules.Register(builder.Services);

        // Shell services
        builder.Services.AddSingleton<Core.Services.IUiDispatcher, UiDispatcher>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<ShellNavigator>();
        builder.Services.AddSingleton<IShellNavigator>(sp => sp.GetRequiredService<ShellNavigator>());
        builder.Services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellNavigator>());
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IClipboardService, ClipboardService>();
        builder.Services.AddSingleton<IDeviceInfo, DeviceInfo>();
        builder.Services.AddSingleton<TrayService>();
        builder.Services.AddSingleton<IUserNotifications>(sp => sp.GetRequiredService<TrayService>());
        builder.Services.AddSingleton<SearchService>();
        builder.Services.AddSingleton<Core.Palette.IPaletteProvider, ShellPaletteProvider>();
        builder.Services.AddSingleton<ShellController>();
        builder.Services.AddSingleton<Updates.VelopackUpdateService>();
        builder.Services.AddSingleton<Core.Services.IUpdateService>(sp => sp.GetRequiredService<Updates.VelopackUpdateService>());

        // Helm's MCP server: the tools of the modules that are on, for Claude (Helm's chats and Claude Code).
        builder.Services.AddSingleton(sp => new Core.Mcp.McpPipeHost(paths, () =>
        {
            var settings = sp.GetRequiredService<ISettingsStoreFactory>().Get<Core.Mcp.McpSettings>(Core.Mcp.McpSettings.StoreId).Current;
            if (!settings.Enabled) return null;
            var modules = sp.GetRequiredService<Core.Modules.IModuleHost>();
            var tools = sp.GetServices<Core.Mcp.IMcpToolProvider>()
                .Where(p => p.ModuleId is not { } id || modules.Find(id)?.IsEnabled == true)
                .SelectMany(p => p.Tools)
                .Where(t => settings.AllowChanges || t.ReadOnly);
            return new Core.Mcp.McpServer(tools, Shell.Services.AppInfo.Version, Core.Mcp.McpEndpoint.Instructions,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Core.Mcp.McpServer>());
        }, sp.GetRequiredService<ILogger<Core.Mcp.McpPipeHost>>()));

        // Shell views and view models
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<UpdatesViewModel>();
        builder.Services.AddSingleton<SyncViewModel>();
        builder.Services.AddSingleton<SyncIndicatorViewModel>();
        builder.Services.AddSingleton<GeneralViewModel>();
        builder.Services.AddSingleton<GeneralPage>();
        builder.Services.AddSingleton<WelcomePage>();
        builder.Services.AddSingleton<WhatsNewPage>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();
        builder.Services.AddSingleton<DiagnosticsPage>();

        return builder.Build();
    }
}
