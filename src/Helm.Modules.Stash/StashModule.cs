using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Stash;

/// <summary>
/// Stash on Windows. The things live in Helm Sync (files as encrypted blobs) and the pages work directly on
/// <see cref="StashStore"/>; the only background work is following Send to clipboard from the other devices. The menu and Quick access open <see cref="StashContentPage"/>;
/// Home → Utilities opens the settings (<see cref="StashPage"/>).
/// </summary>
public sealed class StashModule(StashViewModel viewModel, StashClipboardRelay relay) : HelmModuleBase, IModuleContent
{
    public override string Id => StashIds.ModuleId;
    public override string DisplayName => StashIds.DisplayName;
    public override string Description => StashIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override SymbolRegular Icon => SymbolRegular.Archive24;
    public override ImageSource IconImage => StashLogo.Image;
    public override Type SettingsPageType => typeof(StashPage);
    public Type ContentPageType => typeof(StashContentPage);
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
