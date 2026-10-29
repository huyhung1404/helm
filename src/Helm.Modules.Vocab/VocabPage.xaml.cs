using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab;

/// <summary>Vocabulary's settings (Home → Utilities). The words are on <see cref="VocabContentPage"/>.</summary>
public partial class VocabPage : ModulePageBase
{
    private readonly IShellNavigation _navigation;
    private readonly VocabPopup _popup;
    private readonly VocabViewModel _viewModel;

    public VocabPage(VocabModule module, VocabViewModel viewModel, VocabPopup popup, IShellNavigation navigation)
    {
        _navigation = navigation;
        _popup = popup;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void OpenVocabulary_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(VocabContentPage));

    private void ShowNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_popup.ShowNow()) _viewModel.Message = "No word is broken down yet. Add words, then let an AI agent analyze them.";
    }
}
