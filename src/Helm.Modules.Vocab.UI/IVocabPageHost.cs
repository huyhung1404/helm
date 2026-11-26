using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>What each platform does its own way around the shared Vocabulary page.</summary>
public interface IVocabPageHost
{
    /// <summary>The page came on screen, or the app came back to it (Android re-checks the pop-up's permissions).</summary>
    void Opened();

    /// <summary>
    /// Under "Add a word": where else words are added on this platform ("From anywhere: Quick Capture (Win+Alt+N), then
    /// /v relative." on the PC, a text selection's "Add word" on the phone). Empty: no line.
    /// </summary>
    string AddHint => "";

    /// <summary>"Show a word now" in the settings: the pop-up card right away (the PC's corner card, the phone's overlay). Null: no button.</summary>
    Action? ShowWordNow => null;

    /// <summary>Settings only this platform has, as cards in the settings sheet (the phone's bubble and blocked apps). Null: none.</summary>
    Control? CreatePlatformSettings() => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}
