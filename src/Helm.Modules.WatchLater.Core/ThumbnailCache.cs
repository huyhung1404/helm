using System.Security.Cryptography;
using System.Text;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Thumbnails kept on this device (they are not synced: each device fetches them from the link in the record). Files
/// are named after a hash of the image link, so a video whose thumbnail changes gets the new one. Facebook's image
/// links expire after some days, so a thumbnail is fetched as soon as the video is saved.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int Parallel = 3;

    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots = new(Parallel);
    private readonly Dictionary<string, Task<string?>> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ThumbnailCache(HelmPaths paths, ILogger<ThumbnailCache>? logger = null, HttpMessageHandler? handler = null)
    {
        _folder = Path.Combine(paths.Root, "cache", WatchLaterIds.ModuleId, "thumbnails");
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Helm Watch Later)");
    }

    /// <summary>The file for an image link, if it is here already.</summary>
    public string? Cached(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = PathFor(url);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The file for an image link, fetched if needed; null when it cannot be fetched (tried once per run).</summary>
    public Task<string?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<string?>(null);
        if (Cached(url) is { } cached) return Task.FromResult<string?>(cached);
        lock (_gate)
        {
            if (_failed.Contains(url)) return Task.FromResult<string?>(null);
            if (_pending.TryGetValue(url, out var running)) return running;
            var task = FetchAsync(url);
            _pending[url] = task;
            return task;
        }
    }

    /// <summary>Deletes every cached thumbnail.</summary>
    /// <returns>The bytes freed.</returns>
    public long Clear()
    {
        long freed = 0;
        if (!Directory.Exists(_folder)) return 0;
        foreach (var file in Directory.EnumerateFiles(_folder))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                freed += size;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return freed;
    }

    /// <summary>The bytes the cached thumbnails use.</summary>
    public long Size() =>
        Directory.Exists(_folder) ? Directory.EnumerateFiles(_folder).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }) : 0;

    private async Task<string?> FetchAsync(string url)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new InvalidDataException("The image is too large.");
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("The image is empty or too large.");
            Directory.CreateDirectory(_folder);
            var path = PathFor(url);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Thumbnail download failed: {Message}", ex.Message);
            lock (_gate) _failed.Add(url);
            return null;
        }
        finally
        {
            _slots.Release();
            lock (_gate) _pending.Remove(url);
        }
    }

    private string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        return Path.Combine(_folder, hash + ".img");
    }

    public void Dispose()
    {
        _http.Dispose();
        _slots.Dispose();
    }
}
