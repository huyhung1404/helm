using Android.Content;
using Android.Content.PM;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch on Android. "Helm Scratch" in the share sheet of any app (<see cref="ScratchShareActivity"/>) puts photos, videos,
/// files and text in; it is hidden while the tool is off. Back closes the details or the text box before the page.
/// </summary>
public sealed class ScratchModule(ScratchViewModel viewModel, ScratchClipboardRelay relay, ILogger<ScratchModule> logger) : AndroidModuleBase, IModuleContent, IBackHandler
{
    /// <summary>Stable component name: Android remembers the enabled state by name.</summary>
    public const string ShareActivityName = "com.huyhung1404.helm.ScratchShareActivity";

    public override string Id => ScratchIds.ModuleId;
    public override string DisplayName => ScratchIds.DisplayName;
    public override string Description => ScratchIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override Symbol Icon => Symbol.Note;

    /// <summary>The same vector icon as on Windows (<see cref="ScratchIconShape"/>).</summary>
    public override IImage? IconImage => ScratchIcon.Image;

    public override Type PageType => typeof(ScratchPage);
    public Type ContentPageType => typeof(ScratchContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        SetShareTarget(true);
        // Decrypted copies from the last run (opened, shared) do not stay around.
        _ = Task.Run(viewModel.WipeOpenedCopies, CancellationToken.None);
        // Send to clipboard from the other devices (while Helm runs and syncs).
        relay.Start();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        SetShareTarget(false);
        relay.Stop();
        return Task.CompletedTask;
    }

    /// <summary>Android back: the details, then the text box, close before the page does.</summary>
    public bool HandleBack()
    {
        if (viewModel.IsDetailOpen)
        {
            viewModel.CloseDetail();
            return true;
        }
        if (!viewModel.IsTextBoxOpen) return false;
        viewModel.CancelTextCommand.Execute(null);
        return true;
    }

    private void SetShareTarget(bool enabled)
    {
        try
        {
            var context = AndroidApp.Context;
            var state = enabled ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled;
            context.PackageManager?.SetComponentEnabledSetting(new ComponentName(context, ShareActivityName), state, ComponentEnableOption.DontKillApp);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not {Action} the Scratch share target", enabled ? "show" : "hide");
        }
    }
}
