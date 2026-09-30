using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Watch Later on Windows. While it is on, saved videos get their details in the background and the downloads a phone
/// asked for are fetched. The menu and Quick access open <see cref="WatchLaterContentPage"/>; Home → Utilities opens the
/// settings (<see cref="WatchLaterPage"/>).
/// </summary>
public sealed class WatchLaterModule(MetadataResolver resolver, PcDownloads downloads) : HelmModuleBase, IModuleContent
{
    public override string Id => WatchLaterIds.ModuleId;
    public override string DisplayName => WatchLaterIds.DisplayName;
    public override string Description => WatchLaterIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override SymbolRegular Icon => SymbolRegular.VideoClip24;
    public override ImageSource IconImage => WatchLaterLogo.Image;
    public override Type SettingsPageType => typeof(WatchLaterPage);
    public Type ContentPageType => typeof(WatchLaterContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        resolver.Start();
        downloads.Start();
        return Task.CompletedTask;
    }

    /// <summary>Also runs when Helm exits or updates: running downloads stop (yt-dlp resumes them next time).</summary>
    public override async Task DisableAsync()
    {
        downloads.Stop();
        await resolver.StopAsync().ConfigureAwait(false);
    }
}
