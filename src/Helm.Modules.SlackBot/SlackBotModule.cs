using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.SlackBot;

/// <summary>
/// SlackBot on Windows: reads the user's Slack with their own account (<see cref="SlackBotEngine"/>), sorts it with
/// Claude Code, and keeps what needs them on screen until they answer (<see cref="SlackBotOverlay"/>). The menu and
/// Quick access open <see cref="SlackBotContentPage"/>; Home → Utilities opens the settings (<see cref="SlackBotPage"/>).
/// Everything stays on this PC.
/// </summary>
public sealed class SlackBotModule(SlackBotEngine engine, SlackBotOverlay overlay) : HelmModuleBase, IModuleContent
{
    public const string ModuleId = "slackbot";

    public override string Id => ModuleId;
    public override string DisplayName => "Slack Bot";
    public override string Description => "Reads your Slack, has Claude pick out what needs you, and keeps it on screen until you answer.";
    public override ModuleGroup Group => ModuleGroup.Messages;
    public override SymbolRegular Icon => SymbolRegular.Bot24;
    public override ImageSource IconImage => SlackBotLogo.Image;
    public override Type SettingsPageType => typeof(SlackBotPage);
    public Type ContentPageType => typeof(SlackBotContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        engine.Start();
        overlay.Start();
        return Task.CompletedTask;
    }

    public override async Task DisableAsync()
    {
        overlay.Stop();
        await engine.StopAsync().ConfigureAwait(false);
    }
}
