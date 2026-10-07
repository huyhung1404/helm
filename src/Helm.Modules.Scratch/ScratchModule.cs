using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch on Windows. The things live in Helm Sync (files as encrypted blobs) and the pages work directly on
/// <see cref="ScratchStore"/>; the only background work is following Send to clipboard from the other devices. The menu and Quick access open <see cref="ScratchContentPage"/>;
/// Home → Utilities opens the settings (<see cref="ScratchPage"/>).
/// </summary>
public sealed class ScratchModule(ScratchViewModel viewModel, ScratchClipboardRelay relay) : HelmModuleBase, IModuleContent
{
    public override string Id => ScratchIds.ModuleId;
    public override string DisplayName => ScratchIds.DisplayName;
    public override string Description => ScratchIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override SymbolRegular Icon => SymbolRegular.Archive24;
    public override ImageSource IconImage => ScratchLogo.Image;
    public override Type SettingsPageType => typeof(ScratchPage);
    public Type ContentPageType => typeof(ScratchContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

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
