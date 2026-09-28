using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Core.Modules;

/// <summary>A Helm tool in the Android app: <see cref="IModule"/> plus what the Avalonia shell shows.</summary>
public interface IAndroidModule : IModule
{
    /// <summary>Fluent System Icon, the same set the Windows app uses (WPF-UI SymbolRegular).</summary>
    Symbol Icon { get; }

    /// <summary>The module's page: an Avalonia control resolved from DI (its DataContext is set by the page itself).</summary>
    Type PageType { get; }
}

/// <summary>Convenience base for Android modules; see <see cref="ModuleBase"/>.</summary>
public abstract class AndroidModuleBase : ModuleBase, IAndroidModule
{
    public abstract Symbol Icon { get; }
    public abstract Type PageType { get; }
}

public static class AndroidModuleServices
{
    /// <summary>
    /// Registers a tool: the module singleton (as itself and as <see cref="IAndroidModule"/>) and its page. Pages are
    /// transient: the shell creates one each time it shows the tool (a recreated activity builds its views again),
    /// so state belongs in the view model or the module.
    /// </summary>
    public static IServiceCollection AddAndroidModule<TModule, TPage>(this IServiceCollection services)
        where TModule : class, IAndroidModule
        where TPage : class
    {
        services.AddSingleton<TModule>();
        services.AddSingleton<IAndroidModule>(sp => sp.GetRequiredService<TModule>());
        services.AddTransient<TPage>();
        return services;
    }
}
