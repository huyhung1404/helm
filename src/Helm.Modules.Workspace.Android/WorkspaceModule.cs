using Android.Content.PM;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Workspace;

/// <summary>
/// Workspace Controller on Android: the remote control. Pick a PC, read what Claude answered in each VS Code window,
/// give the next task, allow a tool or answer Claude's question, or start a new session. The PC (Helm with Workspace
/// Controller on) does the typing; both talk through Helm Sync, so they must sync with the same account. While "Run in
/// background" is on, <see cref="WorkspaceWatchService"/> keeps watching and notifies. Back closes a sheet or the plan
/// before the page.
/// </summary>
public sealed class WorkspaceModule : AndroidModuleBase, IModuleContent, IBackHandler
{
    private const int PermissionRequestCode = 0x5720;
    private readonly WorkspaceRemoteViewModel _viewModel;
    private bool _wantWatch;

    public WorkspaceModule(WorkspaceRemoteViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.Settings.Changed += (_, _) => ApplyWatch();
        // Android refuses a foreground service started from the background: try again when Helm is in front.
        ActivityHost.Resumed += (_, _) => ApplyWatch();
    }

    public override string Id => WorkspaceIds.ModuleId;
    public override string DisplayName => "Workspace Controller";
    public override string Description => "Follow the Claude sessions in VS Code on your PCs and give them their next task.";
    public override ModuleGroup Group => ModuleGroup.WindowsAndDesktop;
    public override Symbol Icon => Symbol.PhoneLaptop;

    /// <summary>The same vector icon as on Windows (<see cref="WorkspaceIconShape"/>).</summary>
    public override IImage? IconImage => WorkspaceIcon.Image;

    public override Type PageType => typeof(WorkspacePage);
    public Type ContentPageType => typeof(WorkspaceContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        _wantWatch = true;
        ApplyWatch();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        _wantWatch = false;
        ApplyWatch();
        return Task.CompletedTask;
    }

    /// <summary>Android back: an open sheet or the plan closes before the page does.</summary>
    public bool HandleBack() => _viewModel.HandleBack();

    /// <summary>Runs the background watch exactly while the tool is on and "Run in background" is too.</summary>
    private void ApplyWatch()
    {
        var context = AndroidApp.Context;
        var wanted = _wantWatch && _viewModel.Settings.Current.RunInBackground;
        if (wanted == WorkspaceWatchService.IsRunning) return;
        if (!wanted)
        {
            WorkspaceWatchService.Stop(context);
            return;
        }
        AskNotificationPermission();
        WorkspaceWatchService.Start(context);
    }

    /// <summary>Android 13+: notifications need your permission; asked once, in front.</summary>
    private static void AskNotificationPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || ActivityHost.Current is not { } activity) return;
        if (activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") == Permission.Granted) return;
        var prefs = activity.GetSharedPreferences("helm_workspace", Android.Content.FileCreationMode.Private);
        if (prefs is null || prefs.GetBoolean("asked_notifications", false)) return;
        prefs.Edit()?.PutBoolean("asked_notifications", true)?.Apply();
        activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], PermissionRequestCode);
    }
}
