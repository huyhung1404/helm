using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;

namespace Helm.Modules.Workspace;

/// <summary>
/// Workspace Controller on Android: the remote control. Pick a PC, read what Claude answered in each VS Code window,
/// give the next task or start a new session. The PC (Helm with Workspace Controller on) does the typing; both talk
/// through Helm Sync, so they must sync with the same account. Back closes the conversation before the page.
/// </summary>
public sealed class WorkspaceModule(WorkspaceRemoteViewModel viewModel) : AndroidModuleBase, IModuleContent, IBackHandler
{
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
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => Task.CompletedTask;

    /// <summary>Android back: the open conversation closes before the page does.</summary>
    public bool HandleBack() => viewModel.CloseSession();
}
