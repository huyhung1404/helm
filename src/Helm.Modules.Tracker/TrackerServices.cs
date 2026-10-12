using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

public static class TrackerServices
{
    public static IServiceCollection AddTrackerModule(this IServiceCollection services) =>
        services
            .AddTrackerCore()
            .AddTrackerLinks(typeof(TrackerContentPage))
            .AddHelmModule<TrackerModule, TrackerPage, TrackerViewModel>()
            .AddSingleton<TrackerContentPage>();
}
