using Avalonia.Controls;
using Helm.Core.Desktop;
using Helm.Modules.SlackBot.Ui;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Slack Report itself, opened from the menu, the Quick access tile, Home → Utilities and the list on screen: the shared
/// <see cref="SlackBotView"/>. Its settings are a sheet behind the gear. The Windows-only part is the clipboard (the
/// Slack app's manifest).
/// </summary>
internal sealed class WindowsSlackBotPageHost : ISlackBotPageHost
{
    public WindowsSlackBotPageHost(SlackBotModule module, SlackBotInboxViewModel inbox, SlackBotViewModel settings) =>
        View = new SlackBotView(module, inbox, settings, this);

    /// <summary>The page the shell shows.</summary>
    public Control View { get; }

    public void Opened()
    {
    }

    // A busy clipboard is ignored: the button can be pressed again.
    public void CopyText(string text) => WindowsClipboard.SetText(text);
}
