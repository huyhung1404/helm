using Avalonia.Controls;
using Helm.Core.Ui;

namespace Helm.Modules.Vault.Ui;

/// <summary>What each platform does its own way around the shared Vault page.</summary>
public interface IVaultPageHost
{
    /// <summary>
    /// Settings that exist on this platform only (Android: the autofill service; Windows: the auto-type shortcut), shown
    /// as one more card in the settings sheet. Null: none.
    /// </summary>
    Control? CreatePlatformSettings();

    /// <summary>The platform can lock the vault when the computer locks (Windows): shows that switch.</summary>
    bool CanLockWithSystem { get; }

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>
/// Confirmation questions of the vault (discard what was typed, apply deletions...) as Helm's glass sheet. The platform's
/// IVaultPlatform.ConfirmAsync calls <see cref="ConfirmAsync"/>; the Vault page tells it where to open.
/// </summary>
public static class VaultDialogs
{
    /// <summary>The page on screen (set by <see cref="VaultView"/> while it is shown).</summary>
    public static Control? Anchor { get; set; }

    public static Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        Anchor is { } anchor ? new GlassDialogs(() => anchor).ConfirmAsync(title, message, confirmText) : Task.FromResult(false);
}
