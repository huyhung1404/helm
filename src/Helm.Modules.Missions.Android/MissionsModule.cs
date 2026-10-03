using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions on Android. The data lives in Helm Sync; nothing runs in the background. Back closes the celebration and
/// the new-mission panel before leaving the page.
/// </summary>
public sealed class MissionsModule(MissionsViewModel viewModel) : AndroidModuleBase, IModuleContent, IBackHandler
{
    public override string Id => MissionsIds.ModuleId;
    public override string DisplayName => MissionsIds.DisplayName;
    public override string Description => MissionsIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override Symbol Icon => Symbol.Flag;

    /// <summary>The same vector icon as on Windows (<see cref="MissionsIconShape"/>).</summary>
    public override IImage? IconImage => MissionsIcon.Image;

    public override Type PageType => typeof(MissionsPage);
    public Type ContentPageType => typeof(MissionsContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => Task.CompletedTask;

    /// <summary>Android back: the celebration, then the new-mission panel, close before the page does.</summary>
    public bool HandleBack()
    {
        if (viewModel.HasCelebration)
        {
            viewModel.DismissCelebrationCommand.Execute(null);
            return true;
        }
        if (!viewModel.IsNewOpen) return false;
        viewModel.CloseNewCommand.Execute(null);
        return true;
    }
}
