using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Habits;

public static class HabitsServices
{
    public static IServiceCollection AddHabitsModule(this IServiceCollection services) =>
        services
            .AddHabitsCore()
            .AddAndroidModule<HabitsModule, HabitsContentPage>()
            .AddTransient<HabitsContentPage>();
}
