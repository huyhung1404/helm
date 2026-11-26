using Avalonia.Media;

namespace Helm.ToolIcons;

/// <summary>
/// Each tool's own icon, as Helm draws it: the tools' .UI icons, and the phone app's Avalonia builders linked in (see
/// ToolIcons.props).
/// </summary>
public sealed class ToolIcons : Helm.Home.IToolIcons
{
    public static IImage? Of(string id) => id switch
    {
        "habits" => Helm.Modules.Habits.Ui.HabitsIcon.Image,
        "missions" => Helm.Modules.Missions.Ui.MissionsIcon.Image,
        "notes" => Helm.Modules.Notes.NotesIcon.Image,
        "novel-reader" => Helm.Modules.NovelReader.Ui.NovelReaderIcon.Image,
        "quick-capture" => Helm.Modules.QuickCapture.QuickCaptureIcon.Image,
        "scratch" => Helm.Modules.Scratch.ScratchIcon.Image,
        "ssh" => Helm.Modules.Ssh.Ui.SshIcon.Image,
        "tracker" => Helm.Modules.Tracker.TrackerIcon.Image,
        "vocab" => Helm.Modules.Vocab.Ui.VocabIcon.Image,
        "wallet" => Helm.Modules.Wallet.Ui.WalletIcon.Image,
        "vault" => Helm.Modules.Vault.Ui.VaultIcon.Image,
        "keyboard" => Helm.Modules.Keyboard.Ui.KeyboardIcon.Image,
        "window-zones" => Helm.Modules.WindowZones.Ui.WindowZonesIcon.Image,
        "slackbot" => Helm.Modules.SlackBot.Ui.SlackBotIcon.Image,
        _ => null,
    };

    public IImage? For(string moduleId) => Of(moduleId);
}
