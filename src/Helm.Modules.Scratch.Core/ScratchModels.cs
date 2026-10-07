using System.Text.Json.Serialization;
using Helm.Core.Sync;

namespace Helm.Modules.Scratch;

public static class ScratchIds
{
    /// <summary>Module id on both apps: the settings file (scratch.json) and the enabled-state key.</summary>
    public const string ModuleId = "scratch";

    public const string DisplayName = "Scratch";

    public const string Description = "A scratch space on every device for photos, videos, files and text: drop something in on your phone, take it out on your PC.";
}

public enum ScratchKind
{
    /// <summary>A file of any type, kept as an encrypted blob.</summary>
    File,
    /// <summary>A piece of text (a link, an address, a code), kept in the record itself.</summary>
    Text,
}

/// <summary>What the list shows.</summary>
public enum ScratchFilter
{
    All,
    /// <summary>Photos and videos.</summary>
    Media,
    /// <summary>Files that are neither photos nor videos.</summary>
    Files,
    Text,
    Trash,
}

/// <summary>One thing in Scratch (synced record in <c>scratch.items</c>).</summary>
public sealed record ScratchItem
{
    /// <summary>Longer text is kept as a .txt file instead (records stay small).</summary>
    public const int MaxTextLength = 50_000;

    public const int MaxNameLength = 200;

    /// <summary>A thumbnail larger than this is not kept (it travels inside the record).</summary>
    public const int MaxThumbnailBytes = 48 * 1024;

    public ScratchKind Kind { get; init; }

    /// <summary>The file name with its extension; empty for text.</summary>
    public string Name { get; init; } = "";

    public string MediaType { get; init; } = "application/octet-stream";

    /// <summary>Bytes of the file, or characters of the text.</summary>
    public long Size { get; init; }

    /// <summary>The encrypted file (files only). Its key lives only here, inside the encrypted record.</summary>
    public BlobRef? Blob { get; init; }

    public string? Text { get; init; }

    /// <summary>A small JPEG of a photo or video, made by the device that added it.</summary>
    public byte[]? Thumbnail { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>
    /// Where it sits on the wall, higher first; set when the user drags it somewhere else. Unset: by when it was added
    /// (<see cref="Position"/>).
    /// </summary>
    public double? SortKey { get; init; }

    /// <summary>The key the wall is sorted by, newest (highest) first.</summary>
    [JsonIgnore]
    public double Position => SortKey ?? AddedAt.ToUnixTimeMilliseconds();

    /// <summary>The name of the device it was added on.</summary>
    public string AddedFrom { get; init; } = "";

    /// <summary>In the trash: hidden from the list, restorable, deleted only when the user deletes it from the trash.</summary>
    public bool Trashed { get; init; }

    public DateTimeOffset? TrashedAt { get; init; }

    [JsonIgnore]
    public bool IsImage => Kind == ScratchKind.File && MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsVideo => Kind == ScratchKind.File && MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsMedia => IsImage || IsVideo;

    /// <summary>The file name, or the first line of the text.</summary>
    [JsonIgnore]
    public string DisplayName => Kind == ScratchKind.Text ? ScratchFormat.FirstLine(Text ?? "", 80) : Name;
}
