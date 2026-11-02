using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.WeeklyReport;

/// <summary>
/// Weekly Report on Windows: the user's week from their git commits and their Slack messages (through Slack Bot), written
/// up by Claude Code for their manager and reworked until it is right. Nothing runs in the background. The menu and
/// Quick access open <see cref="WeeklyReportContentPage"/>; Home → Utilities opens <see cref="WeeklyReportPage"/>.
/// </summary>
public sealed class WeeklyReportModule : HelmModuleBase, IModuleContent
{
    public const string ModuleId = "weekly-report";

    public override string Id => ModuleId;
    public override string DisplayName => "Weekly Report";
    public override string Description => "Writes your weekly report for your manager from your commits and Slack messages, and reworks it until it is right.";
    public override ModuleGroup Group => ModuleGroup.Messages;
    public override SymbolRegular Icon => SymbolRegular.DocumentData24;
    public override ImageSource IconImage => WeeklyReportLogo.Image;
    public override Type SettingsPageType => typeof(WeeklyReportPage);
    public Type ContentPageType => typeof(WeeklyReportContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => Task.CompletedTask;
}
