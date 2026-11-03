using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Helm.Embeddings;

public static class EmbeddingServices
{
    /// <summary>
    /// The sentence model in Helm's data folder (models/paraphrase-multilingual-minilm, downloaded when the user asks)
    /// and <see cref="ITextEmbedder"/> on it. Safe to call from every tool that wants it: one model either way.
    /// </summary>
    public static IServiceCollection AddSentenceEmbeddings(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new SentenceModelInstaller(
            new SentenceModelFiles(Path.Combine(sp.GetRequiredService<ISettingsStoreFactory>().Paths.Root, "models", "paraphrase-multilingual-minilm")),
            sp.GetRequiredService<ILogger<SentenceModelInstaller>>()));
        services.TryAddSingleton<SentenceEmbedder>();
        services.TryAddSingleton<ITextEmbedder>(sp => sp.GetRequiredService<SentenceEmbedder>());
        return services;
    }
}
