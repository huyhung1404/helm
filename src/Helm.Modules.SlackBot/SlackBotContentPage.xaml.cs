using System.Windows;
using System.Windows.Controls;
using Helm.Modules.SlackBot.Ui;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Slack Report itself, opened from the menu, the Quick access tile, Home → Utilities and the list on screen (see
/// <see cref="IModuleContent"/>): the shared Avalonia <see cref="SlackBotView"/> inside this WPF page. Its settings are a
/// sheet behind the gear. The Windows-only part is the clipboard (the Slack app's manifest).
/// </summary>
public partial class SlackBotContentPage : Page, ISlackBotPageHost
{
    public SlackBotContentPage(SlackBotModule module, SlackBotInboxViewModel inbox, SlackBotViewModel settings)
    {
        InitializeComponent();
        Host.Show(() => new SlackBotView(module, inbox, settings, this));
    }

    public void Opened()
    {
    }

    public void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy; the button can be pressed again.
        }
    }
}
