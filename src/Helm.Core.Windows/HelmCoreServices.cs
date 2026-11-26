using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Processes;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Helm.Core;

public static class HelmCoreServices
{
    /// <summary>Registers the shared infrastructure every Windows module relies on.</summary>
    public static IServiceCollection AddHelmCore(this IServiceCollection services, HelmPaths paths)
    {
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddHelmCommon(paths);
        services.AddSingleton<IHotkeyManager, HotkeyManager>();
        services.AddSingleton(sp => new Mcp.McpClientConfig(paths, sp.GetRequiredService<Settings.ISettingsStoreFactory>()));
        services.AddSingleton<LowLevelKeyboardHook>();
        services.AddSingleton<LowLevelMouseHook>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IUserActivity, UserActivity>();
        services.AddSingleton<IMonitorService, MonitorService>();
        services.AddSingleton<IInputMethods, InputMethods>();
        services.AddSingleton<IBrowserSites, BrowserSites>();
        services.AddSingleton<IStartupTaskService, StartupTaskService>();
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<IChildProcessLauncher, ChildProcessLauncher>();
        // Claude Code without a window, for the tools' AI helpers.
        services.AddSingleton<Mcp.ClaudeCodeRunner>();
        services.TryAddSingleton<IAppLocation, ProcessAppLocation>();
        services.AddSingleton<IModuleHost, ModuleRegistry>();
        return services;
    }

    /// <summary>
    /// Registers a module: the module singleton (as itself and as <see cref="IHelmModule"/>) and its page's view model.
    /// The page itself is <see cref="IHelmModule.CreatePage"/>.
    /// </summary>
    public static IServiceCollection AddHelmModule<TModule, TViewModel>(this IServiceCollection services)
        where TModule : class, IHelmModule
        where TViewModel : class
    {
        services.AddSingleton<TModule>();
        services.AddSingleton<IHelmModule>(sp => sp.GetRequiredService<TModule>());
        services.AddSingleton<TViewModel>();
        return services;
    }

    /// <summary>A module whose page needs no view model of its own in DI.</summary>
    public static IServiceCollection AddHelmModule<TModule>(this IServiceCollection services)
        where TModule : class, IHelmModule
    {
        services.AddSingleton<TModule>();
        services.AddSingleton<IHelmModule>(sp => sp.GetRequiredService<TModule>());
        return services;
    }
}
