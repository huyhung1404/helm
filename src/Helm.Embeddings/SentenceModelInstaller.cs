using Helm.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Helm.Embeddings;

/// <summary>
/// Downloads the sentence model (<see cref="SentenceModelFiles"/>) when the user asks for it, and removes it; goes on
/// from where a download stopped (see <see cref="PinnedDownload"/>).
/// </summary>
public sealed class SentenceModelInstaller(SentenceModelFiles files, ILogger<SentenceModelInstaller> logger, HttpMessageHandler? handler = null)
    : PinnedDownload(files, "The sentence model", "Helm-Embeddings", logger, handler)
{
    public new SentenceModelFiles Files => files;
}
