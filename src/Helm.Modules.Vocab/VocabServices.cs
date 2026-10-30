using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

public static class VocabServices
{
    public static IServiceCollection AddVocabModule(this IServiceCollection services) =>
        services
            .AddVocabCore()
            .AddSingleton<IVocabAgent, ClaudeCodeVocabAgent>()
            .AddSingleton<VocabPopup>()
            .AddHelmModule<VocabModule, VocabPage, VocabViewModel>()
            .AddSingleton<VocabContentPage>();
}
