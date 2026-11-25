using Helm.Core.Today;

namespace Helm.Modules.Vault.Backup;

/// <summary>
/// Home's alert for the vault's backups: the last one failed, or no device of the account made a good one for a week.
/// Only for a vault that has a backup place on this device; its button opens the vault.
/// </summary>
public sealed class VaultBackupAlertSource : IHomeAlertSource
{
    private readonly VaultBackupService _backup;
    private readonly Action _open;

    public VaultBackupAlertSource(VaultBackupService backup, Action open)
    {
        _backup = backup;
        _open = open;
        backup.Completed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? ModuleId => "vault";

    public event EventHandler? Changed;

    public IReadOnlyList<HomeAlertData> Current()
    {
        if (_backup.Location is null || !_backup.IsOverdue && _backup.LastError is null) return [];
        var last = _backup.LastGoodBackupAnywhere;
        var title = last is { } at
            ? $"The vault has not been backed up for {Math.Max(1, (int)(DateTimeOffset.UtcNow - at).TotalDays)} days"
            : "The vault has never been backed up";
        var detail = _backup.LastError is { } error ? error : "Back up now so a lost device does not lose your passwords.";
        return [new HomeAlertData("vault-backup", title, detail, "Back up", 20)];
    }

    public Task Act(string alertId)
    {
        _open();
        return Task.CompletedTask;
    }
}
