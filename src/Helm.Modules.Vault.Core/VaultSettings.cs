using Helm.Core.Settings;

namespace Helm.Modules.Vault;

/// <summary>Vault options of this device (never synced: the backup folder and lock timing are per device).</summary>
public sealed class VaultSettings : IVersionedSettings
{
    public const string StoreId = "vault";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Lock after this many minutes without using the vault; 0 = only lock on the other triggers.</summary>
    public int AutoLockMinutes { get; set; } = 5;

    /// <summary>Lock when Windows is locked, the user signs out or the PC sleeps.</summary>
    public bool LockOnSystemLock { get; set; } = true;

    /// <summary>Android: lock this many seconds after Helm goes to the background.</summary>
    public int BackgroundLockSeconds { get; set; } = 30;

    /// <summary>Hide the vault from screenshots, screen recording and screen sharing (Windows capture exclusion, Android FLAG_SECURE).</summary>
    public bool ProtectFromScreenCapture { get; set; } = true;

    /// <summary>Clear a copied secret from the clipboard after this many seconds (0 = never).</summary>
    public int ClipboardClearSeconds { get; set; } = 30;

    /// <summary>Ask for the vault password again after this many days of quick unlock (Windows Hello / fingerprint).</summary>
    public int RequirePasswordDays { get; set; } = 14;

    /// <summary>Trashed items are deleted for good after this many days.</summary>
    public int TrashDays { get; set; } = 30;

    /// <summary>Folder (PC) or Storage Access Framework tree URI (Android) of the backup repository; null = not set.</summary>
    public string? BackupLocation { get; set; }

    public int BackupKeepDaily { get; set; } = 30;

    public int BackupKeepMonthly { get; set; } = 12;
}

/// <summary>What this device remembers about unlocking and backing up (settings/vault-device.json; no secrets).</summary>
public sealed class VaultDeviceState : IVersionedSettings
{
    public const string StoreId = "vault-device";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public long LastPasswordUnlockMs { get; set; }

    public int FailedPasswordAttempts { get; set; }

    public long LastFailedAttemptMs { get; set; }

    public int FailedDeviceUnlocks { get; set; }

    /// <summary>The recovery key id the user confirmed having saved (Emergency Kit) on this device.</summary>
    public string? ConfirmedRecoveryId { get; set; }

    public long LastGoodBackupMs { get; set; }

    /// <summary>This device's id in the synced backup marks (<see cref="Backup.VaultBackupService.MarksCollection"/>).</summary>
    public string? BackupDeviceId { get; set; }

    public string? LastBackupError { get; set; }
}
