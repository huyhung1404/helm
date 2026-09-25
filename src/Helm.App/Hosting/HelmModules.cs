using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Hosting;

/// <summary>The one place where modules are wired into the app. Add a line here for every new module.</summary>
internal static class HelmModules
{
    public static void Register(IServiceCollection services)
    {
    }
}
