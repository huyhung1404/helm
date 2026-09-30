using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WatchLater;

/// <summary>What yt-dlp found about a video.</summary>
public sealed record YtDlpProbe(VideoMetadata Metadata, IReadOnlyList<VideoFormat> Formats);

/// <summary>
/// yt-dlp and ffmpeg for Windows, kept in Helm's own folder (%LOCALAPPDATA%\Helm\tools). yt-dlp is fetched from its
/// GitHub releases the first time something is downloaded and updates itself every few days (sites change often and
/// an old yt-dlp stops working). ffmpeg is optional (only for qualities that merge a video and an audio stream) and
/// installed from Settings, or taken from PATH when it is there.
/// </summary>
public sealed class YtDlpTools : IDisposable
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    public static readonly TimeSpan UpdateEvery = TimeSpan.FromDays(3);

    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly ILogger<YtDlpTools> _logger;
    private readonly SemaphoreSlim _install = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public YtDlpTools(HelmPaths paths, ISettingsStoreFactory settings, ILogger<YtDlpTools> logger)
    {
        ToolsFolder = Path.Combine(paths.Root, "tools");
        _settings = settings.Get<WatchLaterSettings>(WatchLaterIds.ModuleId);
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Helm");
    }

    public string ToolsFolder { get; }

    public string YtDlpPath => Path.Combine(ToolsFolder, "yt-dlp.exe");

    public bool HasYtDlp => File.Exists(YtDlpPath);

    /// <summary>The folder with ffmpeg.exe: Helm's own copy, else one on PATH; null when there is none.</summary>
    public string? FfmpegFolder
    {
        get
        {
            if (File.Exists(Path.Combine(ToolsFolder, "ffmpeg.exe"))) return ToolsFolder;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim('"'), "ffmpeg.exe"))) return dir.Trim('"');
                }
                catch (ArgumentException) { }
            }
            return null;
        }
    }

    public bool HasFfmpeg => FfmpegFolder is not null;

    /// <summary>True when ffmpeg is Helm's own copy (it can be removed from Settings).</summary>
    public bool OwnsFfmpeg => File.Exists(Path.Combine(ToolsFolder, "ffmpeg.exe"));

    /// <summary>Raised after yt-dlp or ffmpeg was installed, updated or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Makes sure yt-dlp is here and not too old (fetching or updating it when needed).</summary>
    public async Task EnsureYtDlpAsync(CancellationToken ct)
    {
        await _install.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!HasYtDlp)
            {
                _logger.LogInformation("Downloading yt-dlp");
                await DownloadFileAsync(YtDlpUrl, YtDlpPath, null, ct).ConfigureAwait(false);
                _settings.Update(s => s.YtDlpCheckedAt = DateTimeOffset.UtcNow);
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (_settings.Current.YtDlpCheckedAt is { } at && DateTimeOffset.UtcNow - at < UpdateEvery) return;
            await UpdateCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _install.Release();
        }
    }

    /// <summary>Updates yt-dlp now (Settings → Update).</summary>
    /// <returns>What yt-dlp said, e.g. "yt-dlp is up to date (2026.09.20)".</returns>
    public async Task<string> UpdateAsync(CancellationToken ct)
    {
        await _install.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!HasYtDlp)
            {
                await DownloadFileAsync(YtDlpUrl, YtDlpPath, null, ct).ConfigureAwait(false);
                _settings.Update(s => s.YtDlpCheckedAt = DateTimeOffset.UtcNow);
                Changed?.Invoke(this, EventArgs.Empty);
                return "yt-dlp is installed.";
            }
            return await UpdateCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _install.Release();
        }
    }

    private async Task<string> UpdateCoreAsync(CancellationToken ct)
    {
        var result = await RunAsync(["-U"], null, ct).ConfigureAwait(false);
        _settings.Update(s => s.YtDlpCheckedAt = DateTimeOffset.UtcNow);
        Changed?.Invoke(this, EventArgs.Empty);
        var said = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        _logger.LogInformation("yt-dlp update: {Result}", said);
        return said;
    }

    /// <summary>The version of yt-dlp, or null when it is not installed or does not run.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        if (!HasYtDlp) return null;
        try
        {
            var result = await RunAsync(["--version"], null, ct).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.Output.Trim() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "yt-dlp --version failed");
            return null;
        }
    }

    /// <summary>Installs Helm's own ffmpeg (about 130 MB to download; only ffmpeg.exe and ffprobe.exe are kept).</summary>
    public async Task InstallFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _install.WaitAsync(ct).ConfigureAwait(false);
        var zip = Path.Combine(ToolsFolder, "ffmpeg-download.zip");
        try
        {
            await DownloadFileAsync(FfmpegUrl, zip, progress, ct).ConfigureAwait(false);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var found = 0;
                foreach (var entry in archive.Entries)
                {
                    var name = entry.Name.ToLowerInvariant();
                    if (name is not ("ffmpeg.exe" or "ffprobe.exe") || !entry.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(ToolsFolder, name);
                    entry.ExtractToFile(target + ".tmp", overwrite: true);
                    File.Move(target + ".tmp", target, overwrite: true);
                    found++;
                }
                if (found == 0) throw new InvalidDataException("The ffmpeg download did not contain ffmpeg.exe.");
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            TryDelete(zip);
            _install.Release();
        }
    }

    public void RemoveFfmpeg()
    {
        TryDelete(Path.Combine(ToolsFolder, "ffmpeg.exe"));
        TryDelete(Path.Combine(ToolsFolder, "ffprobe.exe"));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Looks a video up without downloading it: its details and the formats it is offered in.</summary>
    public async Task<YtDlpProbe> ProbeAsync(string url, CancellationToken ct)
    {
        await EnsureYtDlpAsync(ct).ConfigureAwait(false);
        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8" };
        AddCookies(args);
        args.Add("--");
        args.Add(url);
        var result = await RunAsync(args, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException(ErrorText(result.Error));
        return ParseProbe(result.Output);
    }

    /// <summary>Downloads a video into <paramref name="folder"/>.</summary>
    /// <param name="progress">0 to 1, when the size is known; null while it is not.</param>
    /// <returns>The downloaded file.</returns>
    public async Task<string> DownloadAsync(string url, string quality, string folder, Action<double?, string> progress, CancellationToken ct)
    {
        await EnsureYtDlpAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var ffmpeg = FfmpegFolder;
        var args = new List<string>
        {
            "--no-playlist", "--newline", "--progress", "--no-warnings", "--no-mtime", "--encoding", "utf-8",
            "-f", VideoQuality.FormatSelector(quality, ffmpeg is not null),
            "-P", folder,
            "-o", "%(title).150B [%(id)s].%(ext)s",
            "--progress-template", "download:" + ProgressPrefix + "%(progress.downloaded_bytes)s/%(progress.total_bytes)s/%(progress.total_bytes_estimate)s",
            "--print", "after_move:filepath",
        };
        if (ffmpeg is not null)
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg);
            if (quality != VideoQuality.Audio)
            {
                args.Add("--merge-output-format");
                args.Add("mp4");
            }
        }
        AddCookies(args);
        args.Add("--");
        args.Add(url);

        var part = 0;
        double? last = null;
        var result = await RunAsync(args, line =>
        {
            if (ParseProgress(line) is not { } p) return;
            // A merged download fetches the video and then the audio: count the parts so the text says so.
            if (last is > 0.9 && p.Fraction is < 0.1) part++;
            last = p.Fraction;
            progress(p.Fraction, part == 0 ? "Downloading" : "Downloading the audio");
        }, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException(ErrorText(result.Error));
        var file = result.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(ProgressPrefix, StringComparison.Ordinal)).LastOrDefault(File.Exists);
        return file ?? throw new InvalidOperationException("yt-dlp finished but the file was not found.");
    }

    private void AddCookies(List<string> args)
    {
        var browser = _settings.Current.CookiesBrowser;
        if (browser is not ("firefox" or "chrome" or "edge" or "brave")) return;
        args.Add("--cookies-from-browser");
        args.Add(browser);
    }

    // ---- Parsing (internal for tests) ----------------------------------------------------------------------------

    internal const string ProgressPrefix = "HELM:";

    internal sealed record Progress(double? Fraction);

    /// <summary>A progress line: "HELM:123/456/NA" (downloaded / total / estimated total).</summary>
    internal static Progress? ParseProgress(string line)
    {
        var at = line.IndexOf(ProgressPrefix, StringComparison.Ordinal);
        if (at < 0) return null;
        var parts = line[(at + ProgressPrefix.Length)..].Trim().Split('/');
        if (parts.Length < 3) return null;
        static double? Num(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        var done = Num(parts[0]);
        var total = Num(parts[1]) ?? Num(parts[2]);
        if (done is null) return new Progress(null);
        return new Progress(total is > 0 ? Math.Clamp(done.Value / total.Value, 0, 1) : null);
    }

    /// <summary>yt-dlp's -J output.</summary>
    internal static YtDlpProbe ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // A playlist-like page (a Facebook post with one video): take the first entry.
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0) root = entries[0];
        static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        static double? D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var metadata = new VideoMetadata(
            S(root, "title") ?? S(root, "fulltitle"),
            S(root, "channel") ?? S(root, "uploader"),
            S(root, "thumbnail"),
            D(root, "duration") is { } d && d > 0 ? (int)Math.Round(d) : null,
            S(root, "webpage_url"));
        var formats = new List<VideoFormat>();
        if (root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                var vcodec = S(f, "vcodec");
                var acodec = S(f, "acodec");
                var ext = S(f, "ext");
                if (ext is "mhtml" || (S(f, "protocol") ?? "").StartsWith("mhtml", StringComparison.Ordinal)) continue; // storyboards
                var hasVideo = vcodec is not null && vcodec != "none";
                var hasAudio = acodec is not null && acodec != "none";
                // Some sites leave the codecs out: a format with a height is a video with sound.
                if (vcodec is null && acodec is null && D(f, "height") is > 0) hasVideo = hasAudio = true;
                if (!hasVideo && !hasAudio) continue;
                var height = D(f, "height") is { } h && h > 0 ? (int)h : (int?)null;
                var bytes = D(f, "filesize") ?? D(f, "filesize_approx");
                formats.Add(new VideoFormat(height, hasVideo, hasAudio, bytes is > 0 ? (long)bytes.Value : null));
            }
        }
        return new YtDlpProbe(metadata, formats);
    }

    /// <summary>The ERROR line of yt-dlp's output, without the "[site] id:" noise.</summary>
    internal static string ErrorText(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))
                   ?? stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "yt-dlp failed.";
        line = line.StartsWith("ERROR:", StringComparison.Ordinal) ? line[6..].Trim() : line;
        if (line.StartsWith('[') && line.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0) line = line[(colon + 2)..];
        return WatchLaterFormat.Shorten(line, 300);
    }

    // ---- Processes -----------------------------------------------------------------------------------------------

    private sealed record RunResult(int ExitCode, string Output, string Error);

    private async Task<RunResult> RunAsync(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo(YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ToolsFolder,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (error) error.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
        // Flush the asynchronous readers.
        process.WaitForExit();
        lock (output) lock (error) return new RunResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private async Task DownloadFileAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total is > 0) progress?.Report((double)done / total.Value);
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _http.Dispose();
        _install.Dispose();
    }
}
