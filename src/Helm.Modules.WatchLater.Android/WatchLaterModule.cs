using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Watch Later on Android. Videos are saved from the share sheet ("Save to Helm" picks Watch Later for a video link)
/// or pasted on the page; their details are looked up in the background while the tool is on. The phone does not
/// download: Download on PC asks a PC to. Back closes the quality picker before leaving the page.
/// </summary>
public sealed class WatchLaterModule(MetadataResolver resolver, WatchLaterViewModel viewModel) : AndroidModuleBase, IModuleContent, IBackHandler
{
    public override string Id => WatchLaterIds.ModuleId;
    public override string DisplayName => WatchLaterIds.DisplayName;
    public override string Description => WatchLaterIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override Symbol Icon => Symbol.VideoClip;

    /// <summary>The same vector icon as on Windows (<see cref="WatchLaterIconShape"/>).</summary>
    public override IImage? IconImage => WatchLaterIcon.Image;

    public override Type PageType => typeof(WatchLaterPage);
    public Type ContentPageType => typeof(WatchLaterContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        resolver.Start();
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => resolver.StopAsync();

    /// <summary>Android back: an open quality picker closes before the page does.</summary>
    public bool HandleBack()
    {
        if (!viewModel.IsPickerOpen) return false;
        viewModel.ClosePickerCommand.Execute(null);
        return true;
    }
}
