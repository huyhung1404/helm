using Helm.Modules.Vocab.Ui;

namespace Helm.Modules.Vocab;

/// <summary>
/// What the shared <see cref="VocabView"/> needs from Windows: the Quick Capture hint and "Show a word now" (the corner
/// card, <see cref="VocabPopup"/>). Its settings are the sheet behind the gear, with the tool's on/off switch.
/// </summary>
internal sealed class WindowsVocabPageHost(VocabPopup popup, VocabViewModel viewModel) : IVocabPageHost
{
    public void Opened()
    {
    }

    public string AddHint => "Several at once with commas. From anywhere: Quick Capture (Win+Alt+N), then /v relative.";

    /// <summary>The corner card right away; with no word broken down yet there is nothing to show.</summary>
    public Action? ShowWordNow => () =>
    {
        if (!popup.ShowNow()) viewModel.Message = "No word is broken down yet. Add words, then let an AI agent analyze them.";
    };
}
