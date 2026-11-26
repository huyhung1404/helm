using Avalonia.Controls;

namespace Helm.Modules.Wallet.Ui;

/// <summary>What each platform does its own way around the shared Wallet page.</summary>
public interface IWalletPageHost
{
    /// <summary>
    /// The page came on screen, or the app came back to it (Android: the notification access may have changed, the
    /// module checks for bank notifications it could not read).
    /// </summary>
    void Opened();

    /// <summary>Every transaction as CSV (<paramref name="csv"/>): Windows saves it where the user picks, Android shares it.</summary>
    void ExportCsv(string csv);

    /// <summary>The export button's words ("Export…" on Windows, "Share CSV" on a phone).</summary>
    string ExportText => "Export…";

    /// <summary>How Quick Capture opens on this platform, for the "Add spending from anywhere" line.</summary>
    string QuickCaptureHint => "In Quick Capture, type /s 50k coffee for money out or /s +2tr bonus for money in.";

    /// <summary>Settings only this platform has, as cards in the settings sheet. Null: none.</summary>
    Control? CreatePlatformSettings() => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}
