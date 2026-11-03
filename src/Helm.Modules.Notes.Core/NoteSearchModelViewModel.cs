using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Downloads;
using Helm.Core.Services;
using Helm.Embeddings;
using Helm.Shell.Services;

namespace Helm.Modules.Notes;

/// <summary>
/// "Search by meaning" in Notes' settings, on the PC and the phone: whether the sentence model is on this device, its
/// download with progress (it goes on from where it stopped), how many notes are indexed, and removing it. Used on the
/// UI thread; the installer's and the indexer's events are posted through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class NoteSearchModelViewModel : ObservableObject
{
    private readonly SentenceModelInstaller _installer;
    private readonly NoteIndexer _indexer;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";

    public NoteSearchModelViewModel(SentenceModelInstaller installer, NoteIndexer indexer, IUiDispatcher ui, IDialogService dialogs)
    {
        _installer = installer;
        _indexer = indexer;
        _ui = ui;
        _dialogs = dialogs;
        installer.Changed += (_, _) => _ui.Post(Update);
        indexer.Changed += (_, _) => _ui.Post(Update);
        Update();
    }

    /// <summary>The size to download, for the button ("Download (130 MB)").</summary>
    public string Size => Megabytes(_installer.Files.TotalSize);

    public bool CanDownload => !IsInstalled && !IsDownloading;

    [RelayCommand]
    private Task Download() => _installer.InstallAsync();

    [RelayCommand]
    private void Cancel() => _installer.Cancel();

    [RelayCommand]
    private async Task Remove()
    {
        if (!await _dialogs.ConfirmAsync("Remove the search model?",
                $"The model ({Size}) is removed from this device. Notes are searched by their words here until it is downloaded again.", "Remove"))
            return;
        _installer.Remove();
    }

    private void Update()
    {
        IsInstalled = _installer.State == PinnedDownloadState.Installed;
        IsDownloading = _installer.State == PinnedDownloadState.Downloading;
        var total = _installer.Files.TotalSize;
        Progress = total == 0 ? 0 : (double)_installer.Downloaded / total;
        Status = _installer.State switch
        {
            PinnedDownloadState.Installed when _indexer.IsIndexing => $"On this device ({Size}). Reading your notes… {_indexer.Indexed} of {_indexer.Total}.",
            PinnedDownloadState.Installed => $"On this device ({Size}). Notes are found by what they are about; nothing leaves the device.",
            PinnedDownloadState.Downloading => $"Downloading… {Progress:P0} ({Megabytes(_installer.Downloaded)} of {Size})",
            PinnedDownloadState.Failed => $"The download stopped: {_installer.Error} Press Download to go on from where it stopped.",
            _ when _installer.Downloaded > 0 => $"Partly downloaded ({Megabytes(_installer.Downloaded)} of {Size}). Press Download to go on.",
            _ => $"Not on this device: notes are found by their words. Download the model once ({Size}) to find them by what they are about.",
        };
        OnPropertyChanged(nameof(CanDownload));
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";
}
