using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Sync;

namespace Helm.Modules.Workspace;

/// <summary>A photo as the phone made it ready to send: upright, at most <see cref="WorkspaceImages.MaxSide"/> on its longer side.</summary>
public sealed record PickedImage(byte[] Bytes, string MediaType, int Width, int Height);

/// <summary>The phone's camera and gallery (Android); none on the PC.</summary>
public interface IWorkspaceImagePicker
{
    /// <summary>Takes a photo with the camera app; null when you cancelled.</summary>
    Task<PickedImage?> TakePhotoAsync(CancellationToken ct);

    /// <summary>Pictures from the gallery, at most <paramref name="max"/>; empty when you cancelled.</summary>
    Task<IReadOnlyList<PickedImage>> PickAsync(int max, CancellationToken ct);
}

/// <summary>A picture waiting in the composer (already saved as a blob: it uploads while you type).</summary>
public sealed class DraftImage(WorkspaceImage image)
{
    public WorkspaceImage Image { get; } = image;
}

public sealed partial class WorkspaceRemoteViewModel
{
    private readonly BlobStore? _blobs;
    private readonly IWorkspaceImagePicker? _picker;

    /// <summary>Pictures this phone sent, by key: shown from here until the PC has uploaded its own copy.</summary>
    private readonly Dictionary<string, WorkspaceImage> _sentImages = new(StringComparer.Ordinal);

    /// <summary>The pictures to go with the next message.</summary>
    public ObservableCollection<DraftImage> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>The picture shown full screen; null when none.</summary>
    [ObservableProperty] private WorkspaceImage? _viewedImage;

    public bool IsViewingImage => ViewedImage is not null;

    partial void OnViewedImageChanged(WorkspaceImage? value) => Notify(nameof(IsViewingImage));

    /// <summary>This phone can send pictures to the chosen PC (a PC of 0.47 would type the words alone).</summary>
    public bool ImagesSupported => _blobs is not null && _picker is not null && Host?.TakesImages == true;

    /// <summary>The 📎 button: one more picture fits.</summary>
    public bool CanAttach => ImagesSupported && CanType && Attachments.Count < WorkspaceImages.MaxPerMessage && !_attaching;

    public bool IsAttachSheet => Sheet == WorkspaceSheet.Attach;

    private bool _attaching;

    [RelayCommand]
    private async Task TakePhotoAsync()
    {
        Sheet = WorkspaceSheet.None;
        if (_picker is null || !CanAttach) return;
        await AttachAsync(async () => await _picker.TakePhotoAsync(CancellationToken.None).ConfigureAwait(true) is { } photo ? [photo] : []).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PickImagesAsync()
    {
        Sheet = WorkspaceSheet.None;
        if (_picker is null || !CanAttach) return;
        var room = WorkspaceImages.MaxPerMessage - Attachments.Count;
        await AttachAsync(() => _picker.PickAsync(room, CancellationToken.None)).ConfigureAwait(true);
    }

    /// <summary>Saves each picture as a blob at once (encrypted on the phone; it uploads with the next sync).</summary>
    private async Task AttachAsync(Func<Task<IReadOnlyList<PickedImage>>> pick)
    {
        _attaching = true;
        NotifyAttachments();
        try
        {
            var picked = await pick().ConfigureAwait(true);
            foreach (var p in picked.Take(WorkspaceImages.MaxPerMessage - Attachments.Count))
            {
                using var stream = new MemoryStream(p.Bytes, writable: false);
                var blob = await _blobs!.ImportAsync(stream).ConfigureAwait(true);
                Attachments.Add(new DraftImage(new WorkspaceImage { Key = WorkspaceImages.KeyOf(p.Bytes), MediaType = p.MediaType, Width = p.Width, Height = p.Height, Blob = blob }));
            }
            if (picked.Count > 0) _sync?.RequestSync();
        }
        catch (Exception ex) when (ex is IOException or BlobTooLargeException or InvalidOperationException or UnauthorizedAccessException)
        {
            SendFailed = true;
            SendStatus = "The picture could not be added: " + ex.Message;
        }
        finally
        {
            _attaching = false;
            NotifyAttachments();
        }
    }

    [RelayCommand]
    private void RemoveAttachment(DraftImage? draft)
    {
        if (draft is null || !Attachments.Remove(draft)) return;
        // Never sent: its blob goes (one already on the server is cleaned up later).
        if (draft.Image.Blob is { } blob) _blobs?.Discard(blob.Id);
        NotifyAttachments();
    }

    [RelayCommand]
    private void ViewImage(WorkspaceImage? image)
    {
        if (image?.Blob is not null) ViewedImage = image;
    }

    [RelayCommand] private void CloseImage() => ViewedImage = null;

    /// <summary>The pictures of the draft, as the command carries them; the draft is emptied (they are sent now).</summary>
    private IReadOnlyList<WorkspaceImage> TakeAttachments()
    {
        if (Attachments.Count == 0) return [];
        var images = Attachments.Select(a => a.Image).ToList();
        foreach (var image in images) _sentImages[image.Key] = image;
        Attachments.Clear();
        NotifyAttachments();
        return images;
    }

    /// <summary>A picture of the timeline with a blob to show: the PC's, or this phone's own copy of one it sent.</summary>
    private TranscriptItem WithLocalImages(TranscriptItem item)
    {
        if (item.Images.Count == 0 || item.Images.All(i => i.Blob is not null)) return item;
        return item with { Images = item.Images.Select(i => i.Blob is null && _sentImages.TryGetValue(i.Key, out var own) ? own : i).ToList() };
    }

    private void NotifyAttachments()
    {
        Notify(nameof(HasAttachments), nameof(CanAttach), nameof(ImagesSupported));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The pictures of a prompt in the transcript are the ones sent (any order).</summary>
    private static bool SameImages(IReadOnlyList<WorkspaceImage> shown, IReadOnlyList<WorkspaceImage> sent) =>
        shown.Count == sent.Count && shown.Select(i => i.Key).Order(StringComparer.Ordinal).SequenceEqual(sent.Select(i => i.Key).Order(StringComparer.Ordinal));
}
