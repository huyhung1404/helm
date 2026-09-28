using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Helm.App.Android.Hosting;
using Helm.Core;

namespace Helm.App.Android;

[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<App>
{
    public AndroidApp(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    /// <summary>
    /// Runs before any activity, widget or receiver of the process: from here on every component can reach Helm's
    /// services (built on first use) through <see cref="HelmAndroidServices"/>.
    /// </summary>
    public override void OnCreate()
    {
        HelmAndroidServices.Configure(AndroidHost.Build);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
