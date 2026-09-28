using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Helm.App.Android;

[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<App>
{
    public AndroidApp(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
