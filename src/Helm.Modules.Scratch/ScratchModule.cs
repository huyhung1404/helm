using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Modules.Scratch.Ui;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch on Windows. The things live in Helm Sync (files as encrypted blobs) and the pages work directly on
/// <see cref="ScratchStore"/>; the only background work is following Send to clipboard from the other devices. The menu, Quick access and Home → Utilities open its page, the shared <see cref="ScratchView"/>;
/// its settings are a sheet inside it.
/// </summary>
public sealed class ScratchModule(ScratchViewModel viewModel, ScratchClipboardRelay relay, ILoggerFactory loggers) : HelmModuleBase
{
    public override string Id => ScratchIds.ModuleId;
    public override string DisplayName => ScratchIds.DisplayName;
    public override string Description => ScratchIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override Symbol Icon => Symbol.Note;
    public override IImage IconImage => ScratchIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Control CreatePage() =>
        new ScratchView(this, viewModel, new WindowsScratchPageHost(viewModel, loggers.CreateLogger<WindowsScratchPageHost>()));

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        // Decrypted copies from the last run (opened, saved, copied) do not stay around.
        _ = Task.Run(viewModel.WipeOpenedCopies, CancellationToken.None);
        relay.Start();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        relay.Stop();
        return Task.CompletedTask;
    }
}
