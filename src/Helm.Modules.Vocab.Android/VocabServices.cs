using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

public static class VocabServices
{
    public static IServiceCollection AddVocabModule(this IServiceCollection services) =>
        services
            .AddVocabCore()
            // A word added here is analysed at once (a PC agent when one answers in time, else the API).
            .AddSingleton(sp => new VocabQuickAnalyzer(sp.GetRequiredService<VocabStore>(), sp.GetRequiredService<Helm.Core.Ai.IAiText>()))
            // The pop-up card and the bubble over the other apps, and the settings page's part for them.
            .AddSingleton<VocabOverlay>()
            .AddSingleton<VocabOverlaySettingsViewModel>()
            .AddAndroidModule<VocabModule, VocabPage>()
            .AddTransient<VocabContentPage>();
}
