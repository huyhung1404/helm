using System.Collections.Concurrent;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// The pictures of Workspace Controller on the PC. A photo from the phone comes as an encrypted blob: it is read
/// here, saved as a file for a prompt typed into VS Code (Claude opens it with Read), and remembered by its key so the
/// timeline shows it again without uploading it twice. A picture pasted into Claude on the PC is uploaded once from
/// the transcript, so the phone sees it too. A session record names a picture's blob only once the blob is on the
/// server, so a slow upload never holds the timeline back.
/// </summary>
public sealed class WorkspaceImageStore : IDisposable
{
    /// <summary>Picture files for VS Code prompts are kept this long (Claude reads them right away).</summary>
    public static readonly TimeSpan KeepFiles = TimeSpan.FromDays(3);

    /// <summary>Larger pictures in a transcript are not uploaded for the phone.</summary>
    public const long MaxUploadBytes = 12L * 1024 * 1024;

    private readonly BlobStore? _blobs;
    private readonly Action? _requestSync;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, WorkspaceImage> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _importing = new(StringComparer.Ordinal);

    public WorkspaceImageStore(BlobStore? blobs, string folder, ILogger logger, Action? requestSync = null)
    {
        _blobs = blobs;
        _requestSync = requestSync;
        _logger = logger;
        Folder = Path.GetFullPath(folder);
        if (blobs is not null) blobs.Uploaded += OnUploaded;
        Cleanup();
    }

    /// <summary>Where the picture files for VS Code prompts are.</summary>
    public string Folder { get; }

    /// <summary>A picture's blob reached the server (or a new one was made): the sessions are published again. Off the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Pictures can travel (sync is set up on this PC).</summary>
    public bool CanSend => _blobs is not null;

    /// <summary>Remembers a picture whose blob exists (from the phone, or a record published before).</summary>
    public void Remember(WorkspaceImage image)
    {
        if (image.Key.Length == 0 || image.Blob is null) return;
        _known[image.Key] = image;
    }

    /// <summary>The bytes of a picture from the phone (fetched from the server when not on this PC).</summary>
    /// <exception cref="InvalidOperationException">It has no blob, or sync is not set up here.</exception>
    public async Task<byte[]> ReadAsync(WorkspaceImage image, CancellationToken ct)
    {
        if (image.Blob is not { } blob) throw new InvalidOperationException("The picture never reached the sync server.");
        if (_blobs is null) throw new InvalidOperationException("Helm Sync is not set up on this PC.");
        var bytes = await _blobs.ReadAllAsync(blob, ct).ConfigureAwait(false);
        Remember(image);
        return bytes;
    }

    /// <summary>The picture as a file Claude can open; named after its key (the timeline knows it again by that name).</summary>
    public string SaveFile(WorkspaceImage image, byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, image.Key + WorkspaceImages.Extension(image.MediaType));
        if (!File.Exists(path))
        {
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        Cleanup();
        return path;
    }

    /// <summary>True for a picture file Helm saved for a prompt: Claude may read it without asking.</summary>
    public bool IsOwnFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(full), Folder, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(full) is { Length: 32 } && File.Exists(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts uploading the pictures of a fresh transcript read that are not known yet (from their base64 text, or
    /// the file Helm saved for them). Each finished one raises <see cref="Changed"/>.
    /// </summary>
    public void Take(TranscriptView view)
    {
        if (_blobs is null) return;
        foreach (var image in view.Items.SelectMany(i => i.Images))
        {
            if (_known.ContainsKey(image.Key) || !_importing.TryAdd(image.Key, 0)) continue;
            view.ImageData.TryGetValue(image.Key, out var base64);
            var file = base64 is null ? OwnFileOf(image.Key) : null;
            if (base64 is null && file is null)
            {
                _importing.TryRemove(image.Key, out _);
                continue;
            }
            _ = ImportAsync(image, base64, file);
        }
    }

    /// <summary>The timeline as the phone gets it: each picture with its blob once that is on the server.</summary>
    public IReadOnlyList<TranscriptItem> Resolve(IReadOnlyList<TranscriptItem> items)
    {
        if (!items.Any(i => i.Images.Count > 0)) return items;
        return items.Select(i => i.Images.Count == 0 ? i : i with { Images = i.Images.Select(Ready).ToList() }).ToList();
    }

    private WorkspaceImage Ready(WorkspaceImage image)
    {
        if (!_known.TryGetValue(image.Key, out var known) || known.Blob is not { } blob || _blobs?.IsPending(blob.Id) == true) return image with { Blob = null };
        return known;
    }

    private async Task ImportAsync(WorkspaceImage image, string? base64, string? file)
    {
        try
        {
            var bytes = base64 is not null ? Convert.FromBase64String(base64) : await File.ReadAllBytesAsync(file!).ConfigureAwait(false);
            if (bytes.Length > MaxUploadBytes) return;
            using var stream = new MemoryStream(bytes, writable: false);
            var blob = await _blobs!.ImportAsync(stream).ConfigureAwait(false);
            var mediaType = file is not null ? MediaTypeOf(file) : image.MediaType;
            _known[image.Key] = image with { MediaType = mediaType, Blob = blob };
            _logger.LogInformation("Workspace: a picture of a session is uploaded for the phone ({Bytes} bytes)", bytes.Length);
            _requestSync?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or BlobTooLargeException or InvalidOperationException)
        {
            _logger.LogInformation(ex, "Workspace: a picture of a session could not be uploaded for the phone");
        }
        finally
        {
            _importing.TryRemove(image.Key, out _);
        }
    }

    public void Dispose()
    {
        if (_blobs is not null) _blobs.Uploaded -= OnUploaded;
    }

    private void OnUploaded(object? sender, string id)
    {
        if (_known.Values.Any(i => i.Blob?.Id == id)) Changed?.Invoke(this, EventArgs.Empty);
    }

    private string? OwnFileOf(string key)
    {
        try
        {
            return Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, key + ".*").FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.Ordinal)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string MediaTypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>Picture files older than <see cref="KeepFiles"/> go.</summary>
    private void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.UtcNow - KeepFiles;
            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Workspace: old picture files could not be cleaned up");
        }
    }
}
