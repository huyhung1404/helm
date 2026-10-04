using Android.Content;
using Helm.Core.Platform;
using AndroidApp = Android.App.Application;
using Settings = Android.Provider.Settings;
using Uri = Android.Net.Uri;

namespace Helm.Modules.Wallet;

/// <summary>Notification access and the widget, for the shared view model (<see cref="IWalletPlatform"/>).</summary>
internal sealed class AndroidWalletPlatform : IWalletPlatform
{
    private static Context Context => AndroidApp.Context;

    private static ComponentName Listener => new(Context, Java.Lang.Class.FromType(typeof(BankNotificationListener)));

    /// <summary>The listeners the user allowed, as Android keeps them ("pkg/class:pkg/class").</summary>
    public bool HasNotificationAccess
    {
        get
        {
            var allowed = Settings.Secure.GetString(Context.ContentResolver, "enabled_notification_listeners") ?? "";
            var me = Listener.FlattenToString();
            return allowed.Split(':').Any(c => string.Equals(c, me, StringComparison.Ordinal)
                || ComponentName.UnflattenFromString(c) is { } n && n.PackageName == Context.PackageName && n.ClassName == Listener.ClassName);
        }
    }

    public void OpenNotificationAccess()
    {
        // Android 11+ opens Helm's own switch; older versions the list of apps.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var detail = new Intent("android.settings.NOTIFICATION_LISTENER_DETAIL_SETTINGS")
                .PutExtra("android.provider.extra.NOTIFICATION_LISTENER_COMPONENT_NAME", Listener.FlattenToString());
            if (Start(detail)) return;
        }
        Start(new Intent(Settings.ActionNotificationListenerSettings));
    }

    public void OpenAppDetails() =>
        Start(new Intent(Settings.ActionApplicationDetailsSettings, Uri.Parse("package:" + Context.PackageName)));

    public bool CanPinWidget => WalletWidgets.CanRequestPin(Context);

    public void PinWidget() => WalletWidgets.RequestPin(Context);

    private static bool Start(Intent intent)
    {
        try
        {
            if (ActivityHost.Current is { } activity)
            {
                activity.StartActivity(intent);
            }
            else
            {
                intent.AddFlags(ActivityFlags.NewTask);
                Context.StartActivity(intent);
            }
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not open Android's settings");
            return false;
        }
    }
}
