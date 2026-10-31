using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

public static class VocabServices
{
    public static IServiceCollection AddVocabModule(this IServiceCollection services) =>
        services
            .AddVocabCore()
            // The pop-up card and the bubble over the other apps, and the settings page's part for them.
            .AddSingleton<VocabOverlay>()
            .AddSingleton<VocabOverlaySettingsViewModel>()
            .AddAndroidModule<VocabModule, VocabPage>()
            .AddTransient<VocabContentPage>();
}
