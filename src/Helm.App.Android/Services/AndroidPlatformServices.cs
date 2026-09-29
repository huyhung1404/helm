using Android.Content;
using Android.OS;
using Avalonia.Threading;
using Helm.Core.Services;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;

namespace Helm.App.Android.Services;

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}

internal sealed class AndroidClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        if (AndroidApp.Context.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard)
            clipboard.PrimaryClip = ClipData.NewPlainText("Helm", text);
    }
}

internal sealed class AndroidDeviceInfo : IDeviceInfo
{
    /// <summary>"Samsung SM-S918B" style: the manufacturer, then the model unless it already starts with it.</summary>
    public string DeviceName
    {
        get
        {
            var maker = Build.Manufacturer ?? "";
            var model = Build.Model ?? "Android";
            if (maker.Length > 0) maker = char.ToUpperInvariant(maker[0]) + maker[1..];
            return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0 ? model : $"{maker} {model}";
        }
    }
}

/// <summary>Opens links and shares files through Android intents. There are no folders or processes to open.</summary>
public sealed class AndroidLauncher(ILogger<AndroidLauncher> logger) : IProcessLauncher, IFileSharer
{
    /// <summary>Files written only to be shared (declared in Resources/xml/update_paths.xml); replaced on each share.</summary>
    public string ShareFolder => Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "share");

    /// <summary>The FileProvider declared in AndroidManifest.xml (update APKs and logs).</summary>
    public const string FileProviderAuthority = "com.huyhung1404.helm.updates";

    public string ExecutablePath => AndroidApp.Context.PackageName ?? "com.huyhung1404.helm";

    public bool IsElevated => false;

    /// <summary>Android has no file manager to point at; General offers "Share logs" instead.</summary>
    public void OpenFolder(string path) => logger.LogInformation("OpenFolder is not available on Android ({Path})", path);

    public void OpenUrl(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, AndroidUri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            AndroidApp.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Url}", url);
        }
    }

    public void StartNewInstance(string? arguments = null)
    {
        var context = AndroidApp.Context;
        var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (intent is null) return;
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        context.StartActivity(intent);
        Java.Lang.JavaSystem.Exit(0);
    }

    /// <summary>Opens the share sheet for a file under the FileProvider paths (e.g. a log file).</summary>
    public void ShareFile(string path, string mimeType, string title)
    {
        try
        {
            var context = AndroidApp.Context;
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, FileProviderAuthority, new Java.IO.File(path));
            var send = new Intent(Intent.ActionSend);
            send.SetType(mimeType);
            send.PutExtra(Intent.ExtraStream, uri);
            send.AddFlags(ActivityFlags.GrantReadUriPermission);
            var chooser = Intent.CreateChooser(send, title)!;
            chooser.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            context.StartActivity(chooser);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not share {Path}", path);
        }
    }
}
