using Helm.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// Downloads the VieNeu voice (<see cref="VieNeuFiles"/>) when the user asks for it, and removes it; goes on from where
/// a download stopped (see <see cref="PinnedDownload"/>).
/// </summary>
public sealed class VieNeuInstaller(VieNeuFiles files, ILogger<VieNeuInstaller> logger, HttpMessageHandler? handler = null)
    : PinnedDownload(files, "The VieNeu voice", "Helm-NovelReader", logger, handler)
{
    public new VieNeuFiles Files => files;
}
