using System.Text.Json.Serialization;
using Helm.Core.Sync;

namespace Helm.Modules.Stash;

public static class StashIds
{
    /// <summary>Module id on both apps: the settings file (stash.json) and the enabled-state key.</summary>
    public const string ModuleId = "stash";

    public const string DisplayName = "Stash";

    public const string Description = "One place for the photos, videos, files and text you want on every device: put them in on your phone, take them out on your PC.";
}

public enum StashKind
{
    /// <summary>A file of any type, kept as an encrypted blob.</summary>
    File,
    /// <summary>A piece of text (a link, an address, a code), kept in the record itself.</summary>
    Text,
}

/// <summary>What the list shows.</summary>
public enum StashFilter
{
    All,
    /// <summary>Photos and videos.</summary>
    Media,
    /// <summary>Files that are neither photos nor videos.</summary>
    Files,
    Text,
    Trash,
}

/// <summary>One thing in the stash (synced record in <c>stash.items</c>).</summary>
public sealed record StashItem
{
    /// <summary>Longer text is kept as a .txt file instead (records stay small).</summary>
    public const int MaxTextLength = 50_000;

    public const int MaxNameLength = 200;

    /// <summary>A thumbnail larger than this is not kept (it travels inside the record).</summary>
    public const int MaxThumbnailBytes = 48 * 1024;

    public StashKind Kind { get; init; }

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
    public bool IsImage => Kind == StashKind.File && MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsVideo => Kind == StashKind.File && MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsMedia => IsImage || IsVideo;

    /// <summary>The file name, or the first line of the text.</summary>
    [JsonIgnore]
    public string DisplayName => Kind == StashKind.Text ? StashFormat.FirstLine(Text ?? "", 80) : Name;
}
