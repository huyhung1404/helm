using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

public static class TrackerServices
{
    public static IServiceCollection AddTrackerModule(this IServiceCollection services) =>
        services
            .AddTrackerCore()
            .AddAndroidModule<TrackerModule, TrackerPage>();
}
