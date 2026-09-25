using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.AlwaysOnTop;

public static class AlwaysOnTopServices
{
    public static IServiceCollection AddAlwaysOnTopModule(this IServiceCollection services) =>
        services.AddHelmModule<AlwaysOnTopModule, AlwaysOnTopPage, AlwaysOnTopViewModel>();
}
