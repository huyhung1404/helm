using System.Globalization;

namespace Helm.Modules.Scratch;

/// <summary>Display text and file names for Scratch, in the current culture.</summary>
public static class ScratchFormat
{
    /// <summary>"512 B", "3.4 KB", "12.5 MB", "1.25 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
        _ => (bytes / 1073741824.0).ToString("0.##", CultureInfo.CurrentCulture) + " GB",
    };

    /// <summary>"just now", "5 min ago", "14:30" (today), "Yesterday", "28 Sep" (this year) or "28 Sep 2025".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (local.Date == today) return local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        return local.Year == today.Year
            ? local.ToString("d MMM", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>The first non-empty line, shortened to <paramref name="max"/> characters.</summary>
    public static string FirstLine(string text, int max)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>What kind of thing it is, in one word: "Photo", "Video", "Text", "PDF", "ZIP", "File".</summary>
    public static string KindName(ScratchItem item)
    {
        if (item.Kind == ScratchKind.Text) return "Text";
        if (item.IsImage) return "Photo";
        if (item.IsVideo) return "Video";
        if (item.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "Audio";
        var extension = Path.GetExtension(item.Name).TrimStart('.');
        return extension.Length is > 0 and <= 5 ? extension.ToUpperInvariant() : "File";
    }

    /// <summary>
    /// A name that is safe as a file name on Windows and Android: no folders, no reserved characters, at most
    /// <see cref="ScratchItem.MaxNameLength"/> characters with the extension kept.
    /// </summary>
    public static string SafeFileName(string? name, string fallback = "file")
    {
        // The last part after either separator (not Path.GetFileName: on Windows "a:b" would lose "a:" as a drive).
        name = (name ?? "").Replace('\\', '/').Split('/').Last();
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)).Trim().Trim('.', ' ');
        if (safe.Length == 0) safe = fallback;
        if (safe.Length > ScratchItem.MaxNameLength)
        {
            var extension = Path.GetExtension(safe);
            if (extension.Length > 16) extension = "";
            safe = safe[..(ScratchItem.MaxNameLength - extension.Length)].TrimEnd('.', ' ') + extension;
        }
        return safe;
    }

    /// <summary>The media type for a file name, when the platform did not say.</summary>
    public static string MediaTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        ".heif" => "image/heif",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".3gp" => "video/3gpp",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".pdf" => "application/pdf",
        ".txt" or ".log" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".apk" => "application/vnd.android.package-archive",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };

    /// <summary>The platform's media type unless it is missing or generic, then the one the name suggests.</summary>
    public static string PickMediaType(string? reported, string name) =>
        string.IsNullOrWhiteSpace(reported) || reported.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? MediaTypeOf(name)
            : reported.Trim().ToLowerInvariant();

    /// <summary>A name for a file whose source gave none, e.g. "Photo 2026-10-07 143012.jpg".</summary>
    public static string NameFor(string mediaType, DateTimeOffset at)
    {
        var stamp = at.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var (word, extension) = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ("Image", ".png"),
            "image/jpeg" => ("Photo", ".jpg"),
            "image/webp" => ("Image", ".webp"),
            "image/gif" => ("Image", ".gif"),
            "video/mp4" => ("Video", ".mp4"),
            "text/plain" => ("Text", ".txt"),
            _ when mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => ("Image", ""),
            _ when mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => ("Video", ""),
            _ => ("File", ""),
        };
        return $"{word} {stamp}{extension}";
    }
}
