using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Shell.Services;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// The VieNeu voice in Novel Reader's settings, on the PC and the phone: whether it is on this device, the download
/// with its progress (it goes on from where it stopped), and removing it. Used on the UI thread; the installer's
/// events are posted through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class VieNeuVoiceViewModel : ObservableObject
{
    private readonly VieNeuInstaller _installer;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";

    public VieNeuVoiceViewModel(VieNeuInstaller installer, IUiDispatcher ui, IDialogService dialogs)
    {
        _installer = installer;
        _ui = ui;
        _dialogs = dialogs;
        installer.Changed += (_, _) => _ui.Post(Update);
        installer.InstalledChanged += (_, _) => _ui.Post(() => InstalledChanged?.Invoke(this, EventArgs.Empty));
        Update();
    }

    /// <summary>The size to download, for the button ("Download (270 MB)").</summary>
    public string Size => Megabytes(_installer.Files.TotalSize);

    public bool CanDownload => !IsInstalled && !IsDownloading;

    /// <summary>The voice was downloaded or removed (on the UI thread): the voice list changes.</summary>
    public event EventHandler? InstalledChanged;

    [RelayCommand]
    private Task Download() => _installer.InstallAsync();

    [RelayCommand]
    private void Cancel() => _installer.Cancel();

    [RelayCommand]
    private async Task Remove()
    {
        if (!await _dialogs.ConfirmAsync("Remove the VieNeu voice?",
                $"The voice ({Size}) is removed from this device. Reading aloud uses another voice until it is downloaded again.", "Remove"))
            return;
        _installer.Remove();
    }

    private void Update()
    {
        IsInstalled = _installer.State == VieNeuInstallState.Installed;
        IsDownloading = _installer.State == VieNeuInstallState.Downloading;
        var total = _installer.Files.TotalSize;
        Progress = total == 0 ? 0 : (double)_installer.Downloaded / total;
        Status = _installer.State switch
        {
            VieNeuInstallState.Installed => $"On this device ({Size}). It reads offline; nothing leaves the device.",
            VieNeuInstallState.Downloading => $"Downloading… {Progress:P0} ({Megabytes(_installer.Downloaded)} of {Size})",
            VieNeuInstallState.Failed => $"The download stopped: {_installer.Error} Press Download to go on from where it stopped.",
            _ when _installer.Downloaded > 0 => $"Partly downloaded ({Megabytes(_installer.Downloaded)} of {Size}). Press Download to go on.",
            _ => $"Not on this device. Download it once ({Size}); then it reads offline.",
        };
        OnPropertyChanged(nameof(CanDownload));
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";
}
