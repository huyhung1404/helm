using Android.Views;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Microsoft.Extensions.Logging;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Modules.Vault;

/// <summary>
/// Vault on Android. While enabled: FLAG_SECURE keeps the unlocked vault out of screenshots and the Recents preview, the vault locks
/// a while after Helm goes to the background, old trash is emptied and the daily backup runs when unlocked.
/// </summary>
public sealed class VaultModule(
    VaultSession session,
    VaultStore store,
    VaultBackupService backup,
    VaultAppViewModel app,
    ISettingsStoreFactory settings,
    IUiDispatcher ui,
    ILogger<VaultModule> logger) : AndroidModuleBase, IModuleContent, IBackHandler
{
    public const string ModuleId = "vault";
    private readonly ISettingsStore<VaultSettings> _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
    private CancellationTokenSource? _backgroundLock;
    private Timer? _maintenance;

    public override string Id => ModuleId;
    public override string DisplayName => "Vault";
    public override string Description => "Keeps passwords, secure notes, cards and documents encrypted, synced and backed up.";
    public override ModuleGroup Group => ModuleGroup.Advanced;
    public override Symbol Icon => Symbol.ShieldKeyhole;
    public override Avalonia.Media.IImage IconImage => VaultIcon.Image;
    public override Type PageType => typeof(VaultPage);

    public Type ContentPageType => typeof(VaultContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        ActivityHost.Paused += OnPaused;
        ActivityHost.Resumed += OnResumed;
        ActivityHost.Created += OnActivityCreated;
        _settings.Changed += OnSettingsChanged;
        session.PropertyChanged += OnSessionChanged;
        ApplyScreenProtection();
        _maintenance ??= new Timer(_ => ui.Post(RunMaintenance), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        ActivityHost.Paused -= OnPaused;
        ActivityHost.Resumed -= OnResumed;
        ActivityHost.Created -= OnActivityCreated;
        _settings.Changed -= OnSettingsChanged;
        session.PropertyChanged -= OnSessionChanged;
        _backgroundLock?.Cancel();
        _maintenance?.Dispose();
        _maintenance = null;
        session.Lock("Vault turned off");
        ui.Post(() => SetSecure(false));
        return Task.CompletedTask;
    }

    /// <summary>Android back: close an open item before leaving the vault page.</summary>
    public bool HandleBack()
    {
        if (app.Items.Detail is not { } detail) return false;
        // Editing: back cancels the edit (a new item closes); viewing: back closes the item.
        if (detail.IsEditing) detail.CancelCommand.Execute(null);
        else app.Items.CloseDetailCommand.Execute(null);
        return true;
    }

    private void OnPaused(object? sender, EventArgs e)
    {
        var seconds = _settings.Current.BackgroundLockSeconds;
        if (seconds <= 0)
        {
            session.Lock("Helm went to the background");
            return;
        }
        var cts = _backgroundLock = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) session.Lock("Helm stayed in the background");
        }, TaskScheduler.Default);
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        _backgroundLock?.Cancel();
        _backgroundLock = null;
    }

    private void OnActivityCreated(object? sender, AndroidX.AppCompat.App.AppCompatActivity activity) => ApplyScreenProtection();

    private void OnSettingsChanged(object? sender, VaultSettings e) => ui.Post(ApplyScreenProtection);

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultSession.State)) ui.Post(ApplyScreenProtection);
    }

    /// <summary>
    /// Only while the vault is unlocked: then nothing of it shows in screenshots, recordings or the Recents preview
    /// (taken when Helm goes to the background). The rest of Helm stays capturable while the vault is locked.
    /// </summary>
    private void ApplyScreenProtection() => SetSecure(_settings.Current.ProtectFromScreenCapture && session.State == VaultState.Unlocked);

    private void SetSecure(bool secure)
    {
        var window = ActivityHost.Latest?.Window;
        if (window is null) return;
        if (secure) window.AddFlags(WindowManagerFlags.Secure);
        else window.ClearFlags(WindowManagerFlags.Secure);
    }

    private async void RunMaintenance()
    {
        // async void (timer): everything is caught, a failure here must never take Helm down.
        try
        {
            store.PurgeExpired(TimeSpan.FromDays(Math.Max(1, _settings.Current.TrashDays)));
            await backup.BackUpIfDueAsync().ConfigureAwait(true);
            StatusMessage = backup.IsOverdue && session.State != VaultState.NotSetUp
                ? backup.LastGoodBackup is null ? "The vault has no backup yet. Choose a backup folder below." : "The vault has not been backed up for over a week."
                : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vault maintenance failed");
        }
    }
}
