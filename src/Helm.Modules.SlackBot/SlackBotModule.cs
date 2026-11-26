using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Slack Report on Windows: reads the user's Slack with their own account (<see cref="SlackBotEngine"/>), sorts it with
/// Claude Code, and keeps what needs them on screen until they answer (<see cref="SlackBotOverlay"/>). It only shows:
/// it never answers by itself (Fairy posts through the MCP tools, where the user allowed writing). The menu and Quick
/// access and Home → Utilities open its page (<see cref="CreatePage"/>, the shared SlackBotView); its settings are a
/// sheet inside it.
/// Everything stays on this PC. The id stays "slackbot" so settings and the MCP tools carry over.
/// </summary>
public sealed class SlackBotModule(SlackBotEngine engine, SlackBotOverlay overlay, IServiceProvider services) : HelmModuleBase
{
    public const string ModuleId = SlackBotIds.ModuleId;

    public override string Id => ModuleId;
    public override string DisplayName => "Slack Report";
    public override string Description => "Reads your Slack, has Claude pick out what needs you, and keeps it on screen until you answer.";
    public override ModuleGroup Group => ModuleGroup.Messages;
    public override Symbol Icon => Symbol.Bot;
    public override IImage IconImage => Ui.SlackBotIcon.Image;
    public override Control CreatePage() => ActivatorUtilities.CreateInstance<WindowsSlackBotPageHost>(services, this).View;
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
