using Helm.App.Android.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.Hosting;

/// <summary>
/// The one place where tools are wired into the Android app (the Windows app has its own list in
/// src/Helm.App/Hosting/HelmModules.cs). A tool that exists on both platforms is registered in both lists; a
/// PC-only tool such as Claude Chat appears only there. Add a line per tool, e.g. <c>services.AddMyToolModule();</c>.
/// </summary>
internal static class AndroidModules
{
    public static void Register(IServiceCollection services)
    {
        // No Android tools yet.
    }

    /// <summary>Registers a tool: the module singleton (as itself and as <see cref="IAndroidModule"/>) and its page.</summary>
    public static IServiceCollection AddAndroidModule<TModule, TPage>(this IServiceCollection services)
        where TModule : class, IAndroidModule
        where TPage : class
    {
        services.AddSingleton<TModule>();
        services.AddSingleton<IAndroidModule>(sp => sp.GetRequiredService<TModule>());
        services.AddSingleton<TPage>();
        return services;
    }
}
