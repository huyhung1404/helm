using Android.Content;
using Android.Content.PM;

namespace Helm.Modules.NovelReader.Lens;

/// <summary>
/// Whether Helm lists Novel Lens (<see cref="LensService"/>) as an accessibility service at all. Banking apps scan the
/// phone for sideloaded apps that have one, switched on or not, and warn that they "may carry malware"; so the service
/// ships disabled and is enabled only while the user wants the lens. Disabled, it leaves Android's Accessibility
/// settings and the list other apps can read; Android stops it if it was running.
/// </summary>
public static class LensComponent
{
    public const string ServiceClass = "com.huyhung1404.helm.novel.LensService";

    private static ComponentName Name(Context context) => new(context.PackageName!, ServiceClass);

    /// <summary>Listed (enabled by the user in Helm); the manifest's default is disabled.</summary>
    public static bool IsShown(Context context) =>
        context.PackageManager!.GetComponentEnabledSetting(Name(context)) == ComponentEnabledState.Enabled;

    public static void SetShown(Context context, bool shown) =>
        context.PackageManager!.SetComponentEnabledSetting(Name(context),
            shown ? ComponentEnabledState.Enabled : ComponentEnabledState.Default, ComponentEnableOption.DontKillApp);

    /// <summary>The user switched "Helm Novel Lens" on in Android's Accessibility settings.</summary>
    public static bool IsSwitchedOn(Context context)
    {
        var enabled = Android.Provider.Settings.Secure.GetString(context.ContentResolver,
            Android.Provider.Settings.Secure.EnabledAccessibilityServices) ?? "";
        var name = $"{context.PackageName}/{ServiceClass}";
        return enabled.Split(':').Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
