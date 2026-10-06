namespace Helm.Core.Secrets;

/// <summary>A field of a vault item another tool may use, named but without its value.</summary>
/// <param name="ItemUid">The item's stable id (it survives renames).</param>
public sealed record VaultSecretRef(string ItemUid, string ItemTitle, string FieldName)
{
    public string DisplayName => $"{ItemTitle} · {FieldName}";
}

/// <summary>
/// How another tool uses a secret kept in Vault (e.g. SSH signing in with a password or key stored there), without a
/// second copy anywhere. Implemented by Vault when it is installed. Values are read one at a time, only while the vault
/// is unlocked, and never listed; callers must not store or log them.
/// </summary>
public interface IVaultSecrets
{
    bool IsUnlocked { get; }

    /// <summary>Unlocks with Windows Hello or the fingerprint when this device has quick unlock; false otherwise or when cancelled.</summary>
    Task<bool> TryQuickUnlockAsync(CancellationToken ct = default);

    /// <summary>Opens Vault so the user can unlock it with the password.</summary>
    void ShowVault();

    /// <summary>The text fields of the items (names only), for a picker; empty while locked.</summary>
    IReadOnlyList<VaultSecretRef> ListFields();

    /// <summary>The field's value; null when locked, or when the item or field is gone (or is a file).</summary>
    string? Read(string itemUid, string fieldName);
}
