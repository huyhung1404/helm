using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.App.Services;
using Helm.Core.Services;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Helm.App.ViewModels;

internal sealed partial class SyncDeviceItem(SyncDeviceToken token, bool isThisDevice) : ObservableObject
{
    public SyncDeviceToken Token { get; } = token;
    public string Name => IsThisDevice ? $"{Token.Name} (this device)" : Token.Name;
    public bool IsThisDevice { get; } = isThisDevice;
    public bool CanRevoke => !IsThisDevice && Status == "Active";

    public string Status => Token.RevokedAt is not null ? "Revoked"
        : Token.ExpiresAt is { } e && e <= DateTimeOffset.Now ? "Expired"
        : "Active";

    public string Details
    {
        get
        {
            var parts = new List<string> { $"Added {Token.CreatedAt.LocalDateTime:g}" };
            if (Token.LastUsedAt is { } used) parts.Add($"last used {used.LocalDateTime:g}");
            if (Token.ExpiresAt is { } expires) parts.Add($"expires {expires.LocalDateTime:d}");
            if (!Token.Scopes.Contains(SyncScopes.Write)) parts.Add("read only");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>General → Sync: connect this device, protect the account key, see status and manage devices.</summary>
internal sealed partial class SyncViewModel : ObservableObject
{
    private readonly SyncSetupService _setup;
    private readonly SyncEngine _engine;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SyncViewModel> _logger;
    private string? _thisTokenId;
    private bool _refreshPending;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNotConnected), nameof(NeedsKey), nameof(IsReady))]
    private SyncSetupStage _stage;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UseToken))]
    private bool _useInvite = true;

    [ObservableProperty] private string _inviteCode = "";
    [ObservableProperty] private string _accountNameInput = "";
    [ObservableProperty] private string _deviceName = Environment.MachineName;
    [ObservableProperty] private string _tokenInput = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCreatingPassphrase), nameof(IsUnlocking), nameof(IsCheckingPassphrase))]
    private bool? _accountHasPassphrase;

    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _passphraseConfirm = "";
    [ObservableProperty] private bool _useRecoveryKey;
    [ObservableProperty] private string _recoveryKeyInput = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsShowingRecoveryKey))]
    private string? _recoveryKeyToShow;

    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private double _usagePercent;
    [ObservableProperty] private bool _canManageDevices;
    [ObservableProperty] private string _newDeviceName = "";
    [ObservableProperty] private string _newDeviceDays = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNewDeviceToken))]
    private string? _newDeviceToken;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public SyncViewModel(SyncSetupService setup, SyncEngine engine, IUiDispatcher ui, IDialogService dialogs, ILogger<SyncViewModel> logger)
    {
        _setup = setup;
        _engine = engine;
        _ui = ui;
        _dialogs = dialogs;
        _logger = logger;
        _stage = setup.Stage;
        _engine.StatusChanged += (_, _) => _ui.Post(UpdateStatusText);
        _setup.StageChanged += (_, _) => _ui.Post(() => Stage = _setup.Stage);
        UpdateStatusText();
        _ = RefreshAsync();
    }

    public bool IsNotConnected => Stage == SyncSetupStage.NotConnected;
    public bool NeedsKey => Stage == SyncSetupStage.NeedsKey;
    public bool IsReady => Stage == SyncSetupStage.Ready;
    public bool UseToken
    {
        get => !UseInvite;
        set => UseInvite = !value;
    }
    public bool IsCreatingPassphrase => AccountHasPassphrase == false;
    public bool IsUnlocking => AccountHasPassphrase == true;
    public bool IsCheckingPassphrase => AccountHasPassphrase is null;
    public bool IsShowingRecoveryKey => RecoveryKeyToShow is not null;
    public bool HasNewDeviceToken => NewDeviceToken is not null;
    public bool HasError => ErrorMessage is not null;
    public int MinPassphraseLength => SyncKeyVault.MinPassphraseLength;
    public string ServerText => $"Server: {_setup.Server.Host}";

    public ObservableCollection<SyncDeviceItem> Devices { get; } = [];

    partial void OnStageChanged(SyncSetupStage value)
    {
        OnPropertyChanged(nameof(ServerText));
        // A stage change during an action (e.g. Connect) is refreshed once that action ends; see RunAsync.
        if (IsBusy) _refreshPending = true;
        else _ = RefreshAsync();
    }

    [RelayCommand]
    private Task ConnectAsync() => RunAsync(async () =>
    {
        if (UseInvite)
        {
            if (string.IsNullOrWhiteSpace(AccountNameInput)) throw new UserError("Enter a name for your account.");
            if (string.IsNullOrWhiteSpace(DeviceName)) throw new UserError("Enter a name for this device.");
            await _setup.RedeemInviteAsync(InviteCode, AccountNameInput, DeviceName).ConfigureAwait(true);
            InviteCode = "";
        }
        else
        {
            await _setup.ConnectWithTokenAsync(TokenInput).ConfigureAwait(true);
            TokenInput = "";
        }
    });

    [RelayCommand]
    private Task CreatePassphraseAsync() => RunAsync(async () =>
    {
        if (Passphrase != PassphraseConfirm) throw new UserError("The two passphrases do not match.");
        RecoveryKeyToShow = await _setup.CreatePassphraseAsync(Passphrase).ConfigureAwait(true);
        ClearSecrets();
    });

    [RelayCommand]
    private Task UnlockAsync() => RunAsync(async () =>
    {
        if (UseRecoveryKey) await _setup.UnlockWithRecoveryKeyAsync(RecoveryKeyInput).ConfigureAwait(true);
        else await _setup.UnlockWithPassphraseAsync(Passphrase).ConfigureAwait(true);
        ClearSecrets();
    });

    [RelayCommand]
    private void CopyRecoveryKey() => CopyToClipboard(RecoveryKeyToShow);

    [RelayCommand]
    private void DismissRecoveryKey() => RecoveryKeyToShow = null;

    [RelayCommand]
    private Task SyncNowAsync() => RunAsync(async () =>
    {
        await _engine.SyncNowAsync().ConfigureAwait(true);
        await LoadAccountAsync().ConfigureAwait(true);
    });

    [RelayCommand]
    private Task AddDeviceAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewDeviceName)) throw new UserError("Enter a name for the new device.");
        int? days = null;
        if (!string.IsNullOrWhiteSpace(NewDeviceDays))
        {
            if (!int.TryParse(NewDeviceDays, out var d) || d < 1 || d > 3650) throw new UserError("Expiry must be 1–3650 days, or empty for never.");
            days = d;
        }
        var (_, token) = await _setup.AddDeviceAsync(NewDeviceName, days).ConfigureAwait(true);
        NewDeviceToken = token;
        NewDeviceName = "";
        NewDeviceDays = "";
        await LoadDevicesAsync().ConfigureAwait(true);
    });

    [RelayCommand]
    private void CopyNewDeviceToken() => CopyToClipboard(NewDeviceToken);

    [RelayCommand]
    private void DismissNewDeviceToken() => NewDeviceToken = null;

    [RelayCommand]
    private async Task RevokeDeviceAsync(SyncDeviceItem? device)
    {
        if (device is null) return;
        var confirmed = await _dialogs.ConfirmAsync("Remove this device?",
            $"“{device.Token.Name}” will stop syncing immediately. Its local data stays on that device.", "Remove device").ConfigureAwait(true);
        if (!confirmed) return;
        await RunAsync(async () =>
        {
            await _setup.RevokeDeviceAsync(device.Token.Id).ConfigureAwait(true);
            await LoadDevicesAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync("Turn off sync on this device?",
            "This device stops syncing and forgets its token and key. Your data stays here and on your other devices.",
            "Turn off sync").ConfigureAwait(true);
        if (!confirmed) return;
        await RunAsync(async () =>
        {
            await _setup.SignOutAsync(revokeToken: true).ConfigureAwait(true);
            Devices.Clear();
            RecoveryKeyToShow = null;
            NewDeviceToken = null;
            AccountHasPassphrase = null;
        }).ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        if (Stage == SyncSetupStage.NotConnected) return;
        await RunAsync(async () =>
        {
            if (Stage == SyncSetupStage.NeedsKey)
            {
                AccountHasPassphrase = null;
                AccountHasPassphrase = await _setup.HasPassphraseAsync().ConfigureAwait(true);
            }
            await LoadAccountAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task LoadAccountAsync()
    {
        var account = await _setup.GetAccountAsync().ConfigureAwait(true);
        _thisTokenId = account.TokenId;
        AccountName = account.AccountName;
        CanManageDevices = account.CanManageTokens;
        UsagePercent = account.QuotaBytes > 0 ? Math.Min(100, 100.0 * account.UsedBytes / account.QuotaBytes) : 0;
        UsageText = $"{Megabytes(account.UsedBytes)} of {Megabytes(account.QuotaBytes)} MB used";
        if (IsReady && CanManageDevices) await LoadDevicesAsync().ConfigureAwait(true);
    }

    private async Task LoadDevicesAsync()
    {
        var devices = await _setup.ListDevicesAsync().ConfigureAwait(true);
        Devices.Clear();
        foreach (var d in devices.OrderBy(d => d.RevokedAt is not null).ThenBy(d => d.CreatedAt))
            Devices.Add(new SyncDeviceItem(d, d.Id == _thisTokenId));
    }

    private void UpdateStatusText()
    {
        var status = _engine.Status;
        var last = status.LastSyncedAt is { } at ? $" · last synced {at.LocalDateTime:t}" : "";
        StatusText = status.State switch
        {
            SyncState.Syncing => "Syncing…",
            SyncState.Idle => status.LastSyncedAt is null ? "Ready" : "Up to date" + last,
            SyncState.Offline => "Offline — changes are kept and sent later" + last,
            SyncState.Unauthorized => "This device's token was revoked or has expired. Turn off sync and connect again.",
            SyncState.QuotaExceeded => "Storage is full — new changes stay on this device. " + status.LastError,
            SyncState.Error => "Sync problem: " + status.LastError,
            _ => "Not set up",
        };
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
            if (ex is not (UserError or SyncKeyException or SyncRequestException or SyncAuthException or HttpRequestException))
                _logger.LogError(ex, "Sync action failed");
        }
        finally
        {
            IsBusy = false;
            Stage = _setup.Stage;
            if (_refreshPending)
            {
                _refreshPending = false;
                _ = RefreshAsync();
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UserError or SyncKeyException or SyncRequestException => ex.Message,
        SyncAuthException auth when auth.Code == "insufficient_scope" => "This device is not allowed to do that (its token lacks the permission).",
        SyncAuthException => "The server did not accept this token: it was revoked, has expired, or was never issued.",
        HttpRequestException or TaskCanceledException => "Could not reach the sync server. Check your connection and try again.",
        _ => $"Something went wrong: {ex.Message}",
    };

    private void ClearSecrets()
    {
        Passphrase = "";
        PassphraseConfirm = "";
        RecoveryKeyInput = "";
    }

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard busy; the text is still selectable
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#");

    private sealed class UserError(string message) : Exception(message);
}
