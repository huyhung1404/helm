using System.Windows.Controls;
using Helm.Modules.Vocab.Ui;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the
/// shared Avalonia <see cref="VocabView"/> inside this WPF page. Its settings are the sheet behind the gear, with the
/// tool's on/off switch. "See the whole tree" on the corner card opens a word in it (<see cref="VocabPopup"/>).
/// </summary>
public partial class VocabContentPage : Page, IVocabPageHost
{
    private readonly VocabPopup _popup;
    private readonly VocabViewModel _viewModel;

    public VocabContentPage(VocabModule module, VocabViewModel viewModel, VocabPopup popup)
    {
        _popup = popup;
        _viewModel = viewModel;
        InitializeComponent();
        Host.Show(() => new VocabView(module, viewModel, this));
    }

    public void Opened()
    {
    }

    public string AddHint => "Several at once with commas. From anywhere: Quick Capture (Win+Alt+N), then /v relative.";

    /// <summary>The corner card right away; with no word broken down yet there is nothing to show.</summary>
    public Action? ShowWordNow => () =>
    {
        if (!_popup.ShowNow()) _viewModel.Message = "No word is broken down yet. Add words, then let an AI agent analyze them.";
    };
}
