using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Downloads;

/// <summary>Where a download of <see cref="PinnedFiles"/> stands on this device.</summary>
public enum PinnedDownloadState
{
    NotInstalled,
    Downloading,
    Installed,
    Failed,
}

/// <summary>
/// Downloads a set of <see cref="PinnedFiles"/> when the user asks for it, and removes it. A download that stops (no
/// network, the app closed) goes on from where it stopped: each file is fetched into a ".part" file with HTTP ranges,
/// hashed, and only then put in place. One download at a time. <paramref name="what"/> names it in the log ("The
/// VieNeu voice"); <paramref name="userAgent"/> is sent to the server.
/// </summary>
public class PinnedDownload(PinnedFiles files, string what, string userAgent, ILogger logger, HttpMessageHandler? handler = null)
{
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http = CreateClient(handler, userAgent);
    private readonly object _gate = new();
    private CancellationTokenSource? _running;

    public PinnedFiles Files => files;

    public PinnedDownloadState State { get; private set; } = files.IsInstalled ? PinnedDownloadState.Installed : PinnedDownloadState.NotInstalled;

    /// <summary>Bytes downloaded so far, of <see cref="PinnedFiles.TotalSize"/>.</summary>
    public long Downloaded { get; private set; } = files.BytesPresent();

    /// <summary>Why the last download failed, in words (empty otherwise).</summary>
    public string Error { get; private set; } = "";

    /// <summary>The state or the progress changed; may come on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The files were installed or removed: whatever loads them should look again.</summary>
    public event EventHandler? InstalledChanged;

    /// <summary>Downloads what is missing; completes when every file is installed, or with the failure.</summary>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        CancellationTokenSource running;
        lock (_gate)
        {
            if (_running is not null || State == PinnedDownloadState.Installed) return;
            _running = running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        Set(PinnedDownloadState.Downloading, "");
        try
        {
            Directory.CreateDirectory(files.Folder);
            foreach (var file in files.Files)
            {
                if (files.Has(file)) continue;
                await DownloadAsync(file, running.Token).ConfigureAwait(false);
            }
            Downloaded = files.TotalSize;
            Set(PinnedDownloadState.Installed, "");
            InstalledChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Downloaded = files.BytesPresent();
            Set(PinnedDownloadState.NotInstalled, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "{What} could not be downloaded", what);
            Downloaded = files.BytesPresent();
            Set(PinnedDownloadState.Failed, Describe(ex));
        }
        finally
        {
            lock (_gate) _running = null;
            running.Dispose();
        }
    }

    /// <summary>Stops a download; what arrived is kept for next time.</summary>
    public void Cancel()
    {
        lock (_gate) _running?.Cancel();
    }

    /// <summary>Removes the files from this device (after a download is stopped).</summary>
    public void Remove()
    {
        Cancel();
        try
        {
            if (Directory.Exists(files.Folder)) Directory.Delete(files.Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open (in use): what could not go now goes next time.
            logger.LogWarning(ex, "Some of {What} could not be removed", what);
        }
        Downloaded = files.BytesPresent();
        Set(PinnedDownloadState.NotInstalled, "");
        InstalledChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task DownloadAsync(PinnedFile file, CancellationToken ct)
    {
        var path = files.PathOf(file);
        var part = path + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(files.CheckedMarkOf(file));
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size) have = 0;

        if (have < file.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // A server that ignores the range sends the whole file again.
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0;
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var before = files.BytesPresent() - have;
            var buffer = new byte[1 << 16];
            var lastReport = Environment.TickCount64;
            // A connection that sends nothing for a minute is given up (it is tried again from here next time).
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("the download stalled.");
                }
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                have += read;
                if (Environment.TickCount64 - lastReport < 250) continue;
                lastReport = Environment.TickCount64;
                Downloaded = before + have;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        if (new FileInfo(part).Length != file.Size)
            throw new IOException($"{Path.GetFileName(path)} arrived incomplete.");
        if (!string.Equals(await HashAsync(part, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException($"{Path.GetFileName(path)} arrived damaged; it will be downloaded again.");
        }
        File.Move(part, path, overwrite: true);
        await File.WriteAllTextAsync(files.CheckedMarkOf(file), file.Sha256, ct).ConfigureAwait(false);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private void Set(PinnedDownloadState state, string error)
    {
        State = state;
        Error = error;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => $"the server answered {(int)status}.",
        HttpRequestException => "there is no connection.",
        InvalidDataException => error.Message,
        UnauthorizedAccessException => "Helm may not write to its data folder.",
        _ => error.Message,
    };

    private static HttpClient CreateClient(HttpMessageHandler? handler, string userAgent)
    {
        // No overall timeout: a 100 MB file on a slow phone connection takes minutes. Each read is cancelled with the download.
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }
}
