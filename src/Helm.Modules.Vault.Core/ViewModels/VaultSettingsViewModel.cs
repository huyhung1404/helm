using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

public sealed record Choice(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Vault settings page, on both platforms: locking, quick unlock, password and recovery key, backup and restore,
/// and the KeePass export. Actions that need the vault key ask for the vault to be unlocked first.
/// </summary>
public sealed partial class VaultSettingsViewModel : ObservableObject
{
    private readonly VaultSession _session;
    private readonly VaultStore _store;
    private readonly VaultFiles _files;
    private readonly VaultBackupService _backup;
    private readonly VaultAppViewModel _app;
    private readonly IVaultDeviceUnlock _deviceUnlock;
    private readonly IVaultPlatform _platform;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore<VaultSettings> _settings;
    private bool _loading;

    [ObservableProperty] private Choice? _autoLock;
    [ObservableProperty] private bool _lockOnSystemLock;
    [ObservableProperty] private bool _protectFromScreenCapture;
    [ObservableProperty] private Choice? _backgroundLock;
    [ObservableProperty] private Choice? _clipboardClear;
    [ObservableProperty] private Choice? _requirePassword;
    [ObservableProperty] private Choice? _keepDaily;
    [ObservableProperty] private Choice? _keepMonthly;
    [ObservableProperty] private bool _deviceUnlockAvailable;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _error;

    // Typed by the user (password boxes); cleared after each action.
    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _exportPassword = "";
    [ObservableProperty] private string _exportConfirm = "";
    [ObservableProperty] private bool _exportDocuments = true;

    public VaultSettingsViewModel(VaultSession session, VaultStore store, VaultFiles files, VaultBackupService backup,
        VaultAppViewModel app, IVaultDeviceUnlock deviceUnlock, IVaultPlatform platform, IUiDispatcher ui, ISettingsStoreFactory settings)
    {
        _session = session;
        _store = store;
        _files = files;
        _backup = backup;
        _app = app;
        _deviceUnlock = deviceUnlock;
        _platform = platform;
        _ui = ui;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        Load(_settings.Current);
        _settings.Changed += (_, current) => _ui.Post(() => Load(current));
        session.PropertyChanged += (_, _) => _ui.Post(RaiseState);
        backup.Completed += (_, _) => _ui.Post(RaiseState);
        _ = RefreshDeviceUnlockAsync();
    }

    public IReadOnlyList<Choice> AutoLockChoices { get; } =
        [new(1, "1 minute"), new(5, "5 minutes"), new(15, "15 minutes"), new(60, "1 hour"), new(0, "Never (only when Windows locks)")];

    public IReadOnlyList<Choice> BackgroundLockChoices { get; } = [new(0, "Immediately"), new(30, "30 seconds"), new(120, "2 minutes"), new(600, "10 minutes")];

    public IReadOnlyList<Choice> ClipboardChoices { get; } = [new(15, "15 seconds"), new(30, "30 seconds"), new(90, "90 seconds"), new(0, "Never")];

    public IReadOnlyList<Choice> RequirePasswordChoices { get; } = [new(1, "Every day"), new(7, "Every week"), new(14, "Every 2 weeks"), new(30, "Every month")];

    public IReadOnlyList<Choice> KeepDailyChoices { get; } = [new(7, "7 days"), new(30, "30 days"), new(90, "90 days")];

    public IReadOnlyList<Choice> KeepMonthlyChoices { get; } = [new(0, "None"), new(12, "12 months"), new(36, "3 years"), new(120, "10 years")];

    public bool VaultExists => _session.State != VaultState.NotSetUp;

    public bool IsUnlocked => _session.State == VaultState.Unlocked;

    public string StateText => _session.State switch
    {
        VaultState.NotSetUp => "No vault yet. Open Vault to create one or restore one from a backup.",
        VaultState.Locked => "Locked",
        _ => $"Unlocked · {_store.Items().Count} items",
    };

    public string DeviceUnlockName => _deviceUnlock.Name;

    public bool DeviceUnlockEnabled
    {
        get => _session.IsDeviceUnlockEnrolled;
        set
        {
            if (value == _session.IsDeviceUnlockEnrolled) return;
            _ = value ? RunAsync(EnableDeviceUnlockAsync) : RunAsync(() =>
            {
                _session.DisableDeviceUnlock();
                return Task.CompletedTask;
            });
        }
    }

    public string? RecoveryId => _session.Keyring?.RecoveryId;

    public bool RecoveryKitConfirmed => _session.RecoveryKitConfirmed;

    public string BackupLocation => _settings.Current.BackupLocation ?? "Not set";

    public bool HasBackupLocation => _settings.Current.BackupLocation is not null;

    public string BackupStatus => _backup.LastGoodBackup is { } last
        ? "Last good backup " + last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + (_backup.LastError is { } e ? $" · last attempt failed: {e}" : "")
        : _backup.LastError is { } error ? "Backup failed: " + error : "Never backed up";

    public bool BackupOverdue => _backup.IsOverdue;

    [RelayCommand]
    private void OpenVault() => _platform.ShowVault();

    [RelayCommand]
    private void LockNow() => _session.Lock("user");

    [RelayCommand]
    private Task ChooseBackupLocationAsync() => RunAsync(async () =>
    {
        var location = await _platform.PickBackupLocationAsync(CancellationToken.None).ConfigureAwait(true);
        if (location is null) return;
        _settings.Update(s => s.BackupLocation = location);
        _settings.Flush();
        RaiseState();
        if (IsUnlocked) await BackUpCoreAsync().ConfigureAwait(true);
    });

    [RelayCommand]
    private Task BackUpNowAsync() => RunAsync(BackUpCoreAsync);

    /// <summary>Brings back items that were deleted for good, from the newest backup of this vault.</summary>
    [RelayCommand]
    private Task RestoreMissingAsync() => RunAsync(async () =>
    {
        RequireUnlocked();
        var target = _backup.OpenLocation(_settings.Current.BackupLocation ?? "") ?? throw new InvalidOperationException("Choose the backup folder first.");
        var repository = (await VaultBackupRepository.FindAsync(target).ConfigureAwait(true)).FirstOrDefault(r => r.VaultId == _session.VaultId)
            ?? throw new InvalidOperationException("There is no backup of this vault in the backup folder.");
        if (repository.Snapshots.Count == 0) throw new InvalidOperationException("The backup has no snapshot yet.");
        using var key = new VaultKey(_session.Key.Bytes);
        var count = await _backup.RestoreMissingAsync(repository, repository.Snapshots[0], key).ConfigureAwait(true);
        Message = count == 0 ? "Nothing to restore: the vault has every item of the latest backup." : $"Restored {count} items from the backup.";
    });

    [RelayCommand]
    private Task ChangePasswordAsync() => RunAsync(async () =>
    {
        RequireUnlocked();
        if (NewPassword != ConfirmPassword) throw new VaultKeyException("The two new passwords differ.");
        await _session.ChangePasswordAsync(CurrentPassword, NewPassword).ConfigureAwait(true);
        CurrentPassword = NewPassword = ConfirmPassword = "";
        Message = "Password changed. Other devices use it after their next sync.";
    });

    [RelayCommand]
    private Task NewRecoveryKeyAsync() => RunAsync(async () =>
    {
        RequireUnlocked();
        if (!await _platform.ConfirmAsync("New recovery key", "Create a new recovery key? The Emergency Kit you have now stops working.", "Create").ConfigureAwait(true))
            return;
        var recovery = await _session.NewRecoveryKeyAsync(CurrentPassword).ConfigureAwait(true);
        CurrentPassword = "";
        _app.ShowKit(recovery);
        _platform.ShowVault();
    });

    [RelayCommand]
    private Task ExportKdbxAsync() => RunAsync(async () =>
    {
        RequireUnlocked();
        if (ExportPassword != ExportConfirm) throw new VaultKeyException("The two passwords differ.");
        VaultKeyring.ValidateNewPassword(ExportPassword);
        var stream = await _platform.CreateFileAsync($"Helm Vault {DateTime.Now:yyyy-MM-dd}.kdbx", "application/octet-stream", CancellationToken.None).ConfigureAwait(true);
        if (stream is null) return;
        int count;
        await using (stream)
            count = await new VaultKdbxExporter(_store, _files).ExportAsync(stream, new KdbxExportOptions(ExportPassword, ExportDocuments)).ConfigureAwait(true);
        ExportPassword = ExportConfirm = "";
        Message = $"Exported {count} items. Open the file with KeePassXC, KeePass or KeePassDX, using the password you just chose.";
    });

    partial void OnAutoLockChanged(Choice? value) => Save(s => s.AutoLockMinutes = value?.Value ?? 5);
    partial void OnLockOnSystemLockChanged(bool value) => Save(s => s.LockOnSystemLock = value);
    partial void OnProtectFromScreenCaptureChanged(bool value) => Save(s => s.ProtectFromScreenCapture = value);
    partial void OnBackgroundLockChanged(Choice? value) => Save(s => s.BackgroundLockSeconds = value?.Value ?? 30);
    partial void OnClipboardClearChanged(Choice? value) => Save(s => s.ClipboardClearSeconds = value?.Value ?? 30);
    partial void OnRequirePasswordChanged(Choice? value) => Save(s => s.RequirePasswordDays = value?.Value ?? 14);
    partial void OnKeepDailyChanged(Choice? value) => Save(s => s.BackupKeepDaily = value?.Value ?? 30);
    partial void OnKeepMonthlyChanged(Choice? value) => Save(s => s.BackupKeepMonthly = value?.Value ?? 12);

    private async Task EnableDeviceUnlockAsync()
    {
        RequireUnlocked();
        if (!await _session.EnableDeviceUnlockAsync().ConfigureAwait(true)) Message = $"{DeviceUnlockName} was not turned on.";
        OnPropertyChanged(nameof(DeviceUnlockEnabled));
    }

    private async Task BackUpCoreAsync()
    {
        RequireUnlocked();
        var result = await _backup.BackUpAsync().ConfigureAwait(true);
        Message = result.Written
            ? $"Backed up {result.Records} records ({result.ChunksCopied} new document chunks) and verified."
            : "The backup already had this version of the vault; it was verified again.";
        RaiseState();
    }

    private void RequireUnlocked()
    {
        if (_session.State != VaultState.Unlocked) throw new VaultLockedException();
    }

    private async Task RefreshDeviceUnlockAsync()
    {
        try
        {
            var available = await _deviceUnlock.IsAvailableAsync().ConfigureAwait(false);
            _ui.Post(() => DeviceUnlockAvailable = available);
        }
        catch (Exception)
        {
            _ui.Post(() => DeviceUnlockAvailable = false);
        }
    }

    private void Load(VaultSettings s)
    {
        _loading = true;
        AutoLock = Pick(AutoLockChoices, s.AutoLockMinutes);
        LockOnSystemLock = s.LockOnSystemLock;
        ProtectFromScreenCapture = s.ProtectFromScreenCapture;
        BackgroundLock = Pick(BackgroundLockChoices, s.BackgroundLockSeconds);
        ClipboardClear = Pick(ClipboardChoices, s.ClipboardClearSeconds);
        RequirePassword = Pick(RequirePasswordChoices, s.RequirePasswordDays);
        KeepDaily = Pick(KeepDailyChoices, s.BackupKeepDaily);
        KeepMonthly = Pick(KeepMonthlyChoices, s.BackupKeepMonthly);
        _loading = false;
        RaiseState();
    }

    private static Choice Pick(IReadOnlyList<Choice> choices, int value) =>
        choices.FirstOrDefault(c => c.Value == value) ?? new Choice(value, value.ToString(CultureInfo.CurrentCulture));

    private void Save(Action<VaultSettings> apply)
    {
        if (!_loading) _settings.Update(apply);
    }

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(VaultExists), nameof(IsUnlocked), nameof(StateText), nameof(DeviceUnlockEnabled), nameof(RecoveryId),
                     nameof(RecoveryKitConfirmed), nameof(BackupLocation), nameof(HasBackupLocation), nameof(BackupStatus), nameof(BackupOverdue) })
            OnPropertyChanged(name);
    }

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        Error = null;
        Message = null;
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex switch
            {
                VaultLockedException => "Unlock the vault first (Open vault).",
                _ => ex.Message,
            };
        }
        finally
        {
            IsBusy = false;
            RaiseState();
        }
    }
}
