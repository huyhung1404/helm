namespace Helm.Modules.WatchLater;

/// <summary>A quality to download in, as offered before a download starts.</summary>
/// <param name="Id">A <see cref="VideoQuality"/> id: "best", "1080", "720", …, "audio".</param>
/// <param name="Label">"1080p", "Best available", "Audio only".</param>
/// <param name="Detail">"About 85 MB", "Needs ffmpeg", or empty.</param>
/// <param name="Available">False when it cannot be downloaded here (the detail says why).</param>
public sealed record QualityOption(string Id, string Label, string Detail, bool Available);

/// <summary>The download qualities and how they map to yt-dlp formats.</summary>
public static class VideoQuality
{
    public const string Best = "best";
    public const string P2160 = "2160";
    public const string P1440 = "1440";
    public const string P1080 = "1080";
    public const string P720 = "720";
    public const string P480 = "480";
    public const string P360 = "360";
    public const string Audio = "audio";

    /// <summary>Video heights offered, highest first.</summary>
    public static IReadOnlyList<int> Heights { get; } = [2160, 1440, 1080, 720, 480, 360];

    /// <summary>
    /// The choices when the video's own formats are not known (a phone asking a PC to download): every height, the
    /// best and audio. The PC takes the closest quality the video has.
    /// </summary>
    public static IReadOnlyList<QualityOption> Presets { get; } =
    [
        new(Best, "Best available", "", true),
        new(P1080, "1080p", "", true),
        new(P720, "720p", "", true),
        new(P480, "480p", "", true),
        new(P360, "360p", "Smallest file", true),
        new(Audio, "Audio only", "", true),
    ];

    public static string Label(string? id) => id switch
    {
        Best => "Best available",
        Audio => "Audio only",
        null or "" => "Best available",
        _ => id + "p",
    };

    /// <summary>
    /// The yt-dlp format selector for a quality. With ffmpeg the best video stream up to that height is merged with the
    /// best audio; without it only files that already hold both (often 360p on YouTube) can be used.
    /// </summary>
    public static string FormatSelector(string id, bool hasFfmpeg)
    {
        if (id == Audio) return "ba[ext=m4a]/ba/b";
        var limit = int.TryParse(id, out var h) ? $"[height<={h}]" : "";
        return hasFfmpeg
            ? $"bv*{limit}+ba/b{limit}/bv*+ba/b"
            : $"b{limit}/b";
    }

    /// <summary>
    /// The options for a video whose formats are known: each height it has (up to <see cref="Heights"/>), with the
    /// size when known, then audio. Heights that need two streams merged are unavailable without ffmpeg.
    /// </summary>
    /// <param name="formats">The video's formats: (height or null for audio only, has video, has audio, size in bytes or null).</param>
    public static IReadOnlyList<QualityOption> ForFormats(IReadOnlyList<VideoFormat> formats, bool hasFfmpeg)
    {
        var audio = formats.Where(f => f.HasAudio && !f.HasVideo).OrderByDescending(f => f.Bytes ?? 0).FirstOrDefault();
        var options = new List<QualityOption>();
        var videoHeights = formats.Where(f => f.HasVideo && f.Height is > 0).Select(f => f.Height!.Value).ToHashSet();
        foreach (var height in Heights)
        {
            // Offer a height when the video has it, or something between it and the next lower one (e.g. 1920×1012).
            var lower = Heights.FirstOrDefault(x => x < height);
            if (!videoHeights.Any(v => v <= height && v > lower)) continue;
            var combined = formats.Where(f => f.HasVideo && f.HasAudio && f.Height is { } fh && fh <= height && fh > lower).OrderByDescending(f => f.Height).FirstOrDefault();
            var videoOnly = formats.Where(f => f.HasVideo && !f.HasAudio && f.Height is { } fh && fh <= height && fh > lower).OrderByDescending(f => f.Bytes ?? 0).FirstOrDefault();
            var id = height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // The label is the height the video really has in this band (an old video may top out at 240p).
            var label = videoHeights.Where(v => v <= height && v > lower).Max() + "p";
            if (hasFfmpeg)
            {
                long? bytes = videoOnly?.Bytes is { } vb ? vb + (audio?.Bytes ?? 0) : combined?.Bytes;
                options.Add(new QualityOption(id, label, SizeText(bytes), true));
            }
            else if (combined is not null)
            {
                options.Add(new QualityOption(id, label, SizeText(combined.Bytes), true));
            }
            else
            {
                options.Add(new QualityOption(id, label, NeedsFfmpeg, false));
            }
        }
        if (options.Count == 0 && formats.Any(f => f.HasVideo)) options.Add(new QualityOption(Best, "Best available", "", true));
        if (audio is not null || formats.Any(f => f.HasAudio)) options.Add(new QualityOption(Audio, "Audio only", SizeText(audio?.Bytes), true));
        return options;
    }

    /// <summary>The detail of a quality that cannot be downloaded without ffmpeg.</summary>
    public const string NeedsFfmpeg = "Needs ffmpeg";

    /// <summary>
    /// The option to select first: the wanted quality, else the highest available one below it, else the first
    /// video. Audio only is picked only when it was asked for, so Download never gives a sound file by surprise.
    /// </summary>
    public static QualityOption? Preferred(IReadOnlyList<QualityOption> options, string wanted)
    {
        var available = options.Where(o => o.Available).ToList();
        if (available.FirstOrDefault(o => o.Id == wanted) is { } exact) return exact;
        if (int.TryParse(wanted, out var h))
        {
            var below = available.Where(o => int.TryParse(o.Id, out var x) && x <= h).OrderByDescending(o => int.Parse(o.Id)).FirstOrDefault();
            if (below is not null) return below;
        }
        return available.FirstOrDefault(o => o.Id != Audio);
    }

    private static string SizeText(long? bytes) => bytes is > 0 ? "About " + WatchLaterFormat.Size(bytes.Value) : "";
}

/// <summary>One format a video is offered in (from yt-dlp).</summary>
public sealed record VideoFormat(int? Height, bool HasVideo, bool HasAudio, long? Bytes);
