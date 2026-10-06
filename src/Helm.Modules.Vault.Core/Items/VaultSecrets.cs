using Helm.Core.Secrets;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.Items;

/// <summary>
/// Vault's side of <see cref="IVaultSecrets"/>: other tools may name a field of an item and read its value while the
/// vault is unlocked. Files, trashed items and two-factor secrets are not offered. Reading counts as using the vault
/// (it keeps it from locking for being idle).
/// </summary>
public sealed class VaultSecrets(VaultSession session, VaultStore store, Action? showVault = null) : IVaultSecrets
{
    public bool IsUnlocked => session.State == VaultState.Unlocked;

    public async Task<bool> TryQuickUnlockAsync(CancellationToken ct = default)
    {
        if (IsUnlocked) return true;
        if (session.State != VaultState.Locked || !session.CanUseDeviceUnlock) return false;
        try
        {
            return await session.UnlockWithDeviceAsync(ct).ConfigureAwait(true);
        }
        catch (VaultDeviceUnlockException)
        {
            // Quick unlock was removed; the user unlocks with the password in Vault.
            return false;
        }
    }

    public void ShowVault() => showVault?.Invoke();

    public IReadOnlyList<VaultSecretRef> ListFields()
    {
        if (!IsUnlocked) return [];
        try
        {
            return store.Items()
                .SelectMany(e => e.Item.Fields.Where(Usable).Select(f => new VaultSecretRef(e.Uid, e.Item.Title, f.Name)))
                .OrderBy(r => r.ItemTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.FieldName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (VaultLockedException)
        {
            return [];
        }
    }

    public string? Read(string itemUid, string fieldName)
    {
        if (!IsUnlocked) return null;
        try
        {
            if (store.Get(itemUid) is not { Trashed: false } entry) return null;
            var field = entry.Item.Fields.FirstOrDefault(f => f.Name == fieldName && Usable(f));
            if (field is null) return null;
            session.Touch();
            return field.Value;
        }
        catch (VaultLockedException)
        {
            return null;
        }
    }

    private static bool Usable(VaultField field) => !field.IsFile && field.Kind != VaultFieldKind.Totp && field.Value.Length > 0;
}
