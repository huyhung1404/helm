using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Core.Today;
using Helm.General;
using Helm.Home;
using Helm.Shell.Home;
using Helm.Shell.Services;
using Helm.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Shell.Windows;

public static class ShellWindowsServices
{
    /// <summary>
    /// Home and General on Windows. Helm.App registers <see cref="IShellWindowsHost"/> (its tabs) and the sync, update and
    /// background view models; the tools register their own "today" and alerts.
    /// </summary>
    public static IServiceCollection AddShellWindows(this IServiceCollection services)
    {
        services.TryAddSingleton<IToolIcons, Helm.ToolIcons.ToolIcons>();
        services.TryAddSingleton(sp => new ShellTools(sp.GetRequiredService<IModuleHost>().Modules, sp.GetRequiredService<IToolIcons>()));
        services.TryAddSingleton<GeneralSelection>();
        services.TryAddSingleton<IHomeLinks, WindowsShellLinks>();
        services.TryAddSingleton<IGeneralPlatform, WindowsGeneralPlatform>();

        // What needs the user on Home: an update, sync, a shortcut taken.
        services.AddSingleton<IHomeAlertSource>(sp => new HotkeyConflictAlertSource(
            sp.GetRequiredService<Helm.Core.Hotkeys.IHotkeyManager>(), sp.GetRequiredService<IModuleHost>(), sp.GetRequiredService<IShellWindowsHost>()));
        services.AddSingleton<IHomeAlertSource>(sp => new UpdateAlertSource(sp.GetRequiredService<IUpdateService>(), sp.GetRequiredService<IHomeLinks>()));
        services.AddSingleton<IHomeAlertSource>(sp => new SyncAlertSource(sp.GetRequiredService<SyncEngine>(), sp.GetRequiredService<IHomeLinks>()));

        services.TryAddSingleton(sp => new HomeScreen(
            sp.GetRequiredService<ShellTools>(), sp.GetRequiredService<IToolIcons>(), sp.GetServices<ITodaySource>(), sp.GetServices<IHomeAlertSource>(),
            sp.GetServices<Helm.Core.Capture.ICaptureTarget>(), sp.GetRequiredService<IHomeLinks>(), sp.GetRequiredService<IUiDispatcher>(),
            sp.GetService<SyncEngine>()));
        services.TryAddSingleton(sp =>
        {
            var composer = new GeneralComposer(
                sp.GetRequiredService<SyncViewModel>(), sp.GetRequiredService<UpdatesViewModel>(), sp.GetRequiredService<BackgroundActivitiesViewModel>(),
                sp.GetRequiredService<IGeneralPlatform>(), $"v{AppInfo.Version}", isDesktop: true);
            composer.Model.SetTools(sp.GetRequiredService<ShellTools>().All);
            sp.GetRequiredService<GeneralSelection>().Model = composer.Model;
            return composer;
        });

        // General › MCP: AI agents using Helm's tools (this PC serves them).
        services.TryAddSingleton<Mcp.McpPermissionsViewModel>();
        services.TryAddSingleton<Mcp.McpViewModel>();

        // The two pages (WPF, each hosting its shared Avalonia view).
        services.AddSingleton<HomePage>();
        services.AddSingleton<GeneralPage>();
        return services;
    }
}
