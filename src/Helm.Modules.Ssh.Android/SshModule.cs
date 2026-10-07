using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH on Android. Sessions live in <see cref="SshViewModel"/> and keep running while another page is shown; turning
/// the tool off closes them. Key files are a Windows choice: on the phone a key comes from Vault or is this device's.
/// </summary>
public sealed class SshModule : AndroidModuleBase, IModuleContent
{
    private readonly SshViewModel _viewModel;

    public SshModule(SshViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.AllowKeyFiles = false;
    }

    public override string Id => SshIds.ModuleId;
    public override string DisplayName => SshIds.DisplayName;
    public override string Description => SshIds.Description;
    public override ModuleGroup Group => ModuleGroup.SecurityAndServers;
    public override Symbol Icon => Symbol.WindowConsole;

    /// <summary>The same vector icon as on Windows (<see cref="SshIconShape"/>).</summary>
    public override IImage? IconImage => SshIcon.Image;

    public override Type PageType => typeof(SshPage);
    public Type ContentPageType => typeof(SshContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        _viewModel.SetEnabled(true);
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        _viewModel.SetEnabled(false);
        return Task.CompletedTask;
    }
}
