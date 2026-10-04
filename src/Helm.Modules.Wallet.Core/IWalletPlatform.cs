namespace Helm.Modules.Wallet;

/// <summary>
/// What only the phone can do for Wallet: reading other apps' notifications (Android's notification access) and the
/// home-screen widget. Android registers an implementation; Windows has none, and the pages hide those options.
/// </summary>
public interface IWalletPlatform
{
    /// <summary>Whether Helm has notification access (the user grants it in Android's settings).</summary>
    bool HasNotificationAccess { get; }

    /// <summary>Opens Android's "Notification access" list, where the user turns Helm on.</summary>
    void OpenNotificationAccess();

    /// <summary>Opens Helm's App info page (Allow restricted settings, battery).</summary>
    void OpenAppDetails();

    /// <summary>Whether the launcher can place the widget when the app asks.</summary>
    bool CanPinWidget { get; }

    /// <summary>Asks the launcher to add the Wallet widget.</summary>
    void PinWidget();
}
