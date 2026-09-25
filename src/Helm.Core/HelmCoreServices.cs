using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Core;

public static class HelmCoreServices
{
    /// <summary>Registers the shared infrastructure every module relies on.</summary>
    public static IServiceCollection AddHelmCore(this IServiceCollection services, HelmPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<ISettingsStoreFactory>(sp => new SettingsStoreFactory(paths, sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IHotkeyManager, HotkeyManager>();
        services.AddSingleton<IStartupTaskService, StartupTaskService>();
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<IModuleHost, ModuleRegistry>();
        return services;
    }

    /// <summary>
    /// Registers a module: the module singleton (as itself and as <see cref="IHelmModule"/>), its settings page and
    /// the page's view model.
    /// </summary>
    public static IServiceCollection AddHelmModule<TModule, TPage, TViewModel>(this IServiceCollection services)
        where TModule : class, IHelmModule
        where TPage : class
        where TViewModel : class
    {
        services.AddSingleton<TModule>();
        services.AddSingleton<IHelmModule>(sp => sp.GetRequiredService<TModule>());
        services.AddSingleton<TPage>();
        services.AddSingleton<TViewModel>();
        return services;
    }
}
