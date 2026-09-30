using Microsoft.Extensions.Logging;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Windows: what the web pages did not give (often a Facebook video's length and page) is asked from yt-dlp, once it
/// is installed (it is not fetched just for this). Runs after <see cref="HttpVideoMetadataSource"/>.
/// </summary>
internal sealed class YtDlpMetadataSource(YtDlpTools tools, ILogger<YtDlpMetadataSource> logger) : IVideoMetadataSource
{
    public int Order => 200;

    public async Task<VideoMetadata?> FetchAsync(WatchItem item, CancellationToken ct)
    {
        if (!tools.HasYtDlp || item.Source == WatchSource.Other) return null;
        try
        {
            return (await tools.ProbeAsync(item.Url, ct).ConfigureAwait(false)).Metadata;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogInformation("yt-dlp could not look up the video: {Message}", ex.Message);
            return null;
        }
    }
}
