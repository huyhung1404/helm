using System.Security.Cryptography;
using System.Text;
using Helm.Core.Sync;

namespace Helm.Modules.Workspace;

/// <summary>
/// A picture in a prompt: a photo sent from the phone, or one pasted into Claude on the PC. Its bytes travel as an
/// encrypted blob of Helm Sync; the records carry only this reference.
/// </summary>
public sealed record WorkspaceImage
{
    /// <summary>
    /// Names the picture (<see cref="WorkspaceImages.KeyOf(string)"/>): Claude's transcript keeps pictures as base64, so
    /// the PC knows a picture again without uploading it twice.
    /// </summary>
    public string Key { get; init; } = "";

    public string MediaType { get; init; } = "image/jpeg";

    /// <summary>Its size in pixels; 0 when not known (a picture read from the transcript).</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>The encrypted file; null while the PC has not uploaded it yet.</summary>
    public BlobRef? Blob { get; init; }

    // A blob reference holds its key as an array: two copies of one picture are equal by its id.
    public bool Equals(WorkspaceImage? other) =>
        other is not null && Key == other.Key && MediaType == other.MediaType && Width == other.Width && Height == other.Height && Blob?.Id == other.Blob?.Id;

    public override int GetHashCode() => HashCode.Combine(Key, Blob?.Id);
}

/// <summary>Pictures in prompts: how they are named, sized and shown to Claude.</summary>
public static class WorkspaceImages
{
    /// <summary>Pictures per message from the phone.</summary>
    public const int MaxPerMessage = 5;

    /// <summary>The longer side a photo is brought down to before it is sent (Claude scales larger ones down itself).</summary>
    public const int MaxSide = 1568;

    /// <summary>JPEG quality of a photo sent from the phone.</summary>
    public const int JpegQuality = 85;

    /// <summary>A picture's key: the start of the SHA-256 of its base64 text, as Claude's transcript keeps it.</summary>
    public static string KeyOf(string base64) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(base64)))[..32].ToLowerInvariant();

    public static string KeyOf(byte[] bytes) => KeyOf(Convert.ToBase64String(bytes));

    public static string Extension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg",
    };

    private const string MarkerStart = "[Image from the phone - view it with the Read tool: ";

    /// <summary>
    /// The line a picture becomes in a prompt typed into VS Code: its file on the PC, which Claude opens with Read. The
    /// file is named after the picture's key, so the timeline shows the picture again instead of this line.
    /// </summary>
    public static string Marker(string path) => MarkerStart + path + "]";

    /// <summary>The prompt without Helm's picture lines, and the keys of those pictures.</summary>
    public static (string Text, IReadOnlyList<string> Keys) SplitMarkers(string text)
    {
        if (!text.Contains(MarkerStart, StringComparison.Ordinal)) return (text, []);
        var keys = new List<string>();
        var kept = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(MarkerStart, StringComparison.Ordinal) && t.EndsWith(']'))
            {
                var path = t[MarkerStart.Length..^1];
                var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
                var dot = name.LastIndexOf('.');
                keys.Add(dot > 0 ? name[..dot] : name);
            }
            else kept.Add(line);
        }
        return (string.Join("\n", kept).Trim(), keys);
    }
}
