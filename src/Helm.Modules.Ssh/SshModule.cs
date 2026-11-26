using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH on Windows. Sessions live in <see cref="SshViewModel"/>; turning the tool off (or Helm exiting or updating)
/// closes them. The menu, Quick access and Home → Utilities open its page (<see cref="CreatePage"/>, the shared SshView);
/// its settings are a sheet inside it.
/// </summary>
public sealed class SshModule(SshViewModel viewModel, IServiceProvider services) : HelmModuleBase
{
    public override string Id => SshIds.ModuleId;
    public override string DisplayName => SshIds.DisplayName;
    public override string Description => SshIds.Description;
    public override ModuleGroup Group => ModuleGroup.SecurityAndServers;
    public override Symbol Icon => Symbol.WindowConsole;
    public override IImage IconImage => Ui.SshIcon.Image;
    public override Control CreatePage() => ActivatorUtilities.CreateInstance<WindowsSshPageHost>(services, this).View;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        viewModel.SetEnabled(true);
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        viewModel.SetEnabled(false);
        return Task.CompletedTask;
    }
}
