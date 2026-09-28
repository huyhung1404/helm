namespace Helm.Modules.Vault.Session;

/// <summary>The device's key for quick unlock is gone (e.g. a fingerprint was added, or Windows Hello was reset).</summary>
public sealed class VaultDeviceUnlockException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Quick unlock with the platform's user verification: Windows Hello on PC, fingerprint or face on Android. The
/// platform keeps the vault key wrapped by a key only it can use after the user verifies; the vault password is still
/// required every <see cref="VaultSettings.RequirePasswordDays"/> days.
/// </summary>
public interface IVaultDeviceUnlock
{
    /// <summary>Shown in the UI, e.g. "Windows Hello" or "Fingerprint or face".</summary>
    string Name { get; }

    Task<bool> IsAvailableAsync();

    bool IsEnrolled(string vaultId);

    /// <summary>Asks the user to verify, then stores <paramref name="vaultKey"/> wrapped for this device.</summary>
    /// <returns>False when the user cancelled.</returns>
    Task<bool> EnrollAsync(string vaultId, ReadOnlyMemory<byte> vaultKey, CancellationToken ct);

    /// <summary>Asks the user to verify and returns the vault key; null when the user cancelled.</summary>
    /// <exception cref="VaultDeviceUnlockException">The device key is no longer valid; enrollment was removed.</exception>
    Task<byte[]?> UnlockAsync(string vaultId, CancellationToken ct);

    void Remove();
}

/// <summary>Platforms (and tests) without quick unlock.</summary>
public sealed class NoDeviceUnlock : IVaultDeviceUnlock
{
    public string Name => "Quick unlock";

    public Task<bool> IsAvailableAsync() => Task.FromResult(false);

    public bool IsEnrolled(string vaultId) => false;

    public Task<bool> EnrollAsync(string vaultId, ReadOnlyMemory<byte> vaultKey, CancellationToken ct) => Task.FromResult(false);

    public Task<byte[]?> UnlockAsync(string vaultId, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public void Remove() { }
}
