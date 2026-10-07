namespace Helm.Modules.Scratch;

/// <summary>
/// Something to put into Scratch: a picked or shared file, a dropped file, a pasted picture. <see cref="OpenRead"/>
/// gives a new stream on each call (the thumbnail reads it too); the caller disposes it. <see cref="Origin"/> is what the platform needs to make a
/// thumbnail (a file path on Windows, a content URI on Android); null when there is none.
/// </summary>
public sealed record ScratchSource(string Name, string? MediaType, long? Size, Func<Stream> OpenRead, object? Origin = null);

/// <summary>A decrypted copy of a file in Scratch in the app's private temporary folder.</summary>
public sealed record ScratchLocalFile(string Path, string Name, string MediaType);

/// <summary>
/// What Scratch needs from the platform (WPF on Windows, Avalonia on Android). Implementations run on the UI thread,
/// never throw for cancellations (they return an empty list, null or false) and throw
/// <see cref="InvalidOperationException"/> with a sentence for the user when something cannot be done.
/// </summary>
public interface IScratchPlatform
{
    /// <summary>A private folder for decrypted copies; Scratch clears it when it starts.</summary>
    string OpenFolder { get; }

    /// <summary>"Copy" on Windows (the file goes to the clipboard), "Share" on Android (the share sheet).</summary>
    string SendLabel { get; }

    Task<IReadOnlyList<ScratchSource>> PickFilesAsync(CancellationToken ct);

    /// <summary>A small JPEG (at most 360 px on its longer side) for a photo or a video; null when none can be made.</summary>
    Task<byte[]?> ThumbnailAsync(ScratchSource source, CancellationToken ct);

    /// <summary>Opens the file with the app the system picks for its type.</summary>
    Task OpenAsync(ScratchLocalFile file, CancellationToken ct);

    /// <summary>
    /// Saves a copy where the user wants it (a save dialog on Windows; Pictures, Movies or Download on Android).
    /// </summary>
    /// <returns>Where it went, for a message; null when the user cancelled.</returns>
    Task<string?> SaveAsync(ScratchLocalFile file, CancellationToken ct);

    /// <summary>
    /// Puts a file on the clipboard, so it can be pasted into another app (Send to clipboard, also when another device
    /// sent it). On Windows a photo goes there as a picture too.
    /// </summary>
    Task CopyToClipboardAsync(ScratchLocalFile file, CancellationToken ct);

    /// <summary>Hands the files on: to the clipboard (Windows) or to the share sheet (Android).</summary>
    /// <returns>A short message for the user, or null.</returns>
    Task<string?> SendAsync(IReadOnlyList<ScratchLocalFile> files, CancellationToken ct);
}
