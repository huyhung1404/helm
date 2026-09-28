using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Sync;

namespace Helm.Shell.ViewModels;

/// <summary>What a sync status means for people (shared by the General page and the sync button).</summary>
public static class SyncStatusText
{
    public static string Describe(SyncStatus status)
    {
        var last = status.LastSyncedAt is { } at ? $" · last synced {at.LocalDateTime:t}" : "";
        return status.State switch
        {
            SyncState.Syncing => "Syncing…",
            SyncState.Idle => status.LastSyncedAt is null ? "Ready" : "Up to date" + last,
            SyncState.Offline => "Offline — changes are kept and sent later" + last,
            SyncState.Unauthorized => "This device's token was revoked or has expired. Turn off sync and connect again.",
            SyncState.QuotaExceeded => "Storage is full — new changes stay on this device. " + status.LastError,
            SyncState.KeyChanged => "The account key was changed on another device. Unlock with your passphrase to continue.",
            SyncState.Held => "Sync is paused: another device deleted many records. Review the change in the tool that owns them.",
            SyncState.Error => "Sync problem: " + status.LastError,
            _ => "Not set up",
        };
    }
}

/// <summary>
/// The sync button of the shell (Windows title bar, Android app bar), on every page: its look says whether everything
/// is synced, syncing, offline or has a problem, the tooltip says more, and a click syncs now (or, when sync is not set
/// up, opens its settings).
/// </summary>
public sealed partial class SyncIndicatorViewModel : ObservableObject
{
    private readonly ISyncService _sync;
    private readonly IUiDispatcher _ui;

    [ObservableProperty] private SyncState _state;
    [ObservableProperty] private string _toolTip = "";

    public SyncIndicatorViewModel(ISyncService sync, IUiDispatcher ui)
    {
        _sync = sync;
        _ui = ui;
        _sync.StatusChanged += (_, _) => _ui.Post(Update);
        Update();
    }

    /// <summary>Set by the shell: opens General with the sync settings.</summary>
    public Action? OpenSettings { get; set; }

    public bool IsSyncing => State == SyncState.Syncing;

    public bool IsSynced => State == SyncState.Idle;

    public bool IsNotSetUp => State == SyncState.NotConfigured;

    public bool IsOffline => State == SyncState.Offline;

    /// <summary>Anything that needs the user (token, quota, key, held deletions, errors).</summary>
    public bool HasProblem => State is SyncState.Unauthorized or SyncState.QuotaExceeded or SyncState.KeyChanged or SyncState.Held or SyncState.Error;

    partial void OnStateChanged(SyncState value)
    {
        OnPropertyChanged(nameof(IsSyncing));
        OnPropertyChanged(nameof(IsSynced));
        OnPropertyChanged(nameof(IsNotSetUp));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(HasProblem));
    }

    private void Update()
    {
        var status = _sync.Status;
        State = status.State;
        ToolTip = status.State == SyncState.NotConfigured
            ? "Sync is not set up: your data stays on this device. Click to set it up."
            : SyncStatusText.Describe(status) + (status.State == SyncState.Syncing ? "" : " — click to sync now");
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (State == SyncState.NotConfigured)
        {
            OpenSettings?.Invoke();
            return;
        }
        if (HasProblem && State is SyncState.Unauthorized or SyncState.KeyChanged)
        {
            // Nothing a retry can fix: the settings say what to do.
            OpenSettings?.Invoke();
            return;
        }
        await _sync.SyncNowAsync().ConfigureAwait(true);
    }
}
