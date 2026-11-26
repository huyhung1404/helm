using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Home;
using Helm.Shell.ViewModels;

namespace Helm.General;

/// <summary>
/// General, filled from Helm's real view models: <see cref="SyncViewModel"/>, <see cref="UpdatesViewModel"/> and
/// <see cref="BackgroundActivitiesViewModel"/> (the same ones on every platform) show through <see cref="GeneralViewModel"/>
/// as lines and switches, and its buttons press theirs. What needs more than a line (connect, unlock, recovery key,
/// passphrase change) is shown from the real <see cref="SyncViewModel"/> itself.
/// </summary>
public sealed class GeneralComposer : IGeneralHost
{
    private readonly SyncViewModel _sync;
    private readonly UpdatesViewModel _updates;
    private readonly BackgroundActivitiesViewModel _background;
    private readonly IGeneralPlatform _platform;
    private bool _mirroring;

    public GeneralComposer(SyncViewModel sync, UpdatesViewModel updates, BackgroundActivitiesViewModel background, IGeneralPlatform platform, string version, bool isDesktop)
    {
        _sync = sync;
        _updates = updates;
        _background = background;
        _platform = platform;
        Model = new GeneralViewModel(this, version, isDesktop);
        Model.AttachSync(sync);

        sync.PropertyChanged += (_, _) => MirrorSync();
        sync.Devices.CollectionChanged += (_, _) => MirrorDevices();
        sync.Groups.CollectionChanged += (_, _) => MirrorTargets();
        updates.PropertyChanged += (_, _) => MirrorUpdates();
        background.Items.CollectionChanged += (_, _) => MirrorJobs();
        Model.PropertyChanged += (_, e) => PushSettings(e.PropertyName);
        platform.PropertyChanged += (_, _) => MirrorPlatform();

        MirrorSync();
        MirrorDevices();
        MirrorTargets();
        MirrorUpdates();
        MirrorJobs();
        MirrorPlatform();
        Model.SettingsFolder = platform.SettingsFolder;
    }

    public GeneralViewModel Model { get; }

    // ----- real view models → General

    private void MirrorSync()
    {
        Model.AccountName = _sync.AccountName;
        Model.SyncServer = _sync.ServerText;
        Model.Usage = _sync.UsageText;
        Model.SyncStatus = _sync.Stage switch
        {
            SyncSetupStage.NotConnected => "Not connected",
            SyncSetupStage.NeedsKey => "Needs your passphrase",
            _ => _sync.StatusText,
        };
        Model.Section("sync").NeedsAttention = _sync.Stage != SyncSetupStage.Ready || _sync.HasHold || _sync.HasError;
    }

    private void MirrorDevices()
    {
        Model.Devices.Clear();
        foreach (var device in _sync.Devices)
        {
            var item = device;
            Model.Devices.Add(new SyncDevice(device.Name, device.Details, device.IsThisDevice, _ =>
            {
                if (_sync.RevokeDeviceCommand.CanExecute(item)) _sync.RevokeDeviceCommand.Execute(item);
                return Task.CompletedTask;
            }));
        }
    }

    private void MirrorTargets()
    {
        Model.SyncTargets.Clear();
        foreach (var group in _sync.Groups)
        {
            var target = new SyncTarget(group.Name, group.IsSynced);
            var source = group;
            target.PropertyChanged += (_, e) =>
            {
                if (!_mirroring && e.PropertyName == nameof(SyncTarget.IsSynced)) source.IsSynced = target.IsSynced;
            };
            source.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SyncGroupItem.IsSynced)) Mirroring(() => target.IsSynced = source.IsSynced);
            };
            Model.SyncTargets.Add(target);
        }
    }

    private void MirrorUpdates()
    {
        Mirroring(() =>
        {
            Model.UpdateTitle = _updates.StatusText;
            Model.UpdateSubtitle = _updates.LastCheckedText;
            Model.UpdateAction = _updates.IsDownloaded ? _updates.ApplyActionText : _updates.CanDownload ? "Download" : "Check for updates";
            Model.ChannelIndex = Math.Max(0, _updates.Channels.ToList().IndexOf(_updates.Channel));
            Model.AutoDownload = _updates.AutoDownload;
            Model.InstallOnRestart = _updates.AutoInstallOnRestart;
            Model.DeleteSettingsOnUninstall = _updates.PurgeDataOnUninstall;
        });
        Model.Section("updates").NeedsAttention = _updates.HasError;
    }

    private void MirrorPlatform()
    {
        Mirroring(() =>
        {
            Model.ThemeIndex = _platform.ThemeIndex;
            Model.RunAtStartup = _platform.RunAtStartup;
            Model.StartMinimized = _platform.StartMinimized;
            Model.RunAsAdministrator = _platform.RunAsAdministrator;
        });
    }

    private void MirrorJobs()
    {
        Model.Jobs.Clear();
        foreach (var row in _background.Items)
        {
            var item = row;
            Model.Jobs.Add(new BackgroundJob(row.Title, row.Detail, row.Since, _ =>
            {
                item.StopCommand.Execute(null);
                return Task.CompletedTask;
            }));
        }
    }

    private void Mirroring(Action change)
    {
        var was = _mirroring;
        _mirroring = true;
        try { change(); }
        finally { _mirroring = was; }
    }

    // ----- General → real view models

    private void PushSettings(string? property)
    {
        if (_mirroring) return;
        switch (property)
        {
            case nameof(GeneralViewModel.ChannelIndex) when Model.ChannelIndex >= 0 && Model.ChannelIndex < _updates.Channels.Count:
                _updates.Channel = _updates.Channels[Model.ChannelIndex];
                break;
            case nameof(GeneralViewModel.AutoDownload):
                _updates.AutoDownload = Model.AutoDownload;
                break;
            case nameof(GeneralViewModel.InstallOnRestart):
                _updates.AutoInstallOnRestart = Model.InstallOnRestart;
                break;
            case nameof(GeneralViewModel.ThemeIndex):
                _platform.ThemeIndex = Model.ThemeIndex;
                break;
            case nameof(GeneralViewModel.RunAtStartup):
                _platform.RunAtStartup = Model.RunAtStartup;
                break;
            case nameof(GeneralViewModel.StartMinimized):
                _platform.StartMinimized = Model.StartMinimized;
                break;
            case nameof(GeneralViewModel.DeleteSettingsOnUninstall):
                _updates.PurgeDataOnUninstall = Model.DeleteSettingsOnUninstall;
                break;
        }
    }

    public Task SyncNowAsync()
    {
        _sync.SyncNowCommand.Execute(null);
        return Task.CompletedTask;
    }

    public Task PairDeviceAsync(string deviceName)
    {
        _sync.NewDeviceName = deviceName;
        _sync.AddDeviceCommand.Execute(null);
        return Task.CompletedTask;
    }

    public Task ChangePassphraseAsync()
    {
        _sync.StartChangePassphraseCommand.Execute(null);
        return Task.CompletedTask;
    }

    public Task TurnOffSyncAsync()
    {
        _sync.SignOutCommand.Execute(null);
        return Task.CompletedTask;
    }

    public Task UpdateAsync()
    {
        if (_updates.IsDownloaded) _updates.RestartToUpdateCommand.Execute(null);
        else if (_updates.CanDownload) _updates.DownloadCommand.Execute(null);
        else _updates.CheckCommand.Execute(null);
        return Task.CompletedTask;
    }

    public void OpenSettingsFolder() => _platform.OpenSettingsFolder();

    public void OpenLogs() => _platform.OpenLogs();

    public Task ResetAllAsync() => _platform.ResetAllAsync();
}
