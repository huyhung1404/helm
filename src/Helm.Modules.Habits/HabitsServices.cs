using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Habits;

public static class HabitsServices
{
    public static IServiceCollection AddHabitsModule(this IServiceCollection services) =>
        services
            .AddHabitsCore()
            .AddHelmModule<HabitsModule, HabitsPage, HabitsViewModel>()
            .AddSingleton<HabitsContentPage>();
}
