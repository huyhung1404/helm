using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

public static class VocabServices
{
    public static IServiceCollection AddVocabModule(this IServiceCollection services) =>
        services
            .AddVocabCore()
            .AddAndroidModule<VocabModule, VocabPage>()
            .AddTransient<VocabContentPage>();
}
