using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Helm.Core.Modules;
using Helm.Core.Services;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the words stay visible but read-only, with a note pointing to the settings (Home → Utilities). On the Learn
/// tab the keyboard answers: Space shows the answer, 1 Forgot, 2 Remembered, L listens, S skips, K "I know it"; a Type
/// card puts the cursor in its box, and Enter there checks the word. A word opened on the Words tab takes the page:
/// Esc goes back to the list, ← and → open the word before and after it; the same for a family on the Roots tab, where
/// Space shows a practised root's meaning, then the next root. Ctrl+N opens Add words; Esc closes any dialog first.
/// </summary>
public partial class VocabContentPage : Page
{
    private readonly VocabModule _module;
    private readonly VocabViewModel _viewModel;
    private readonly IShellNavigation _navigation;
    private string? _shownWordId;
    private string? _shownPartId;
    private VocabWordDetailViewModel? _watchedDetail;

    public VocabContentPage(VocabModule module, VocabViewModel viewModel, IShellNavigation navigation)
    {
        _module = module;
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Card.PropertyChanged += OnCardChanged;
        viewModel.Texts.PropertyChanged += OnTextsChanged;
        viewModel.Roots.PropertyChanged += OnRootsChanged;
        // A new page starts at the top of its list.
        viewModel.List.Pages.Turned += (_, _) => Dispatcher.BeginInvoke(() => WordsView.WordsList.BringIntoView(new Rect(0, 0, 1, 1)));
        viewModel.Roots.Pages.Turned += (_, _) => Dispatcher.BeginInvoke(() => RootsView.RootsPanel.BringIntoView(new Rect(0, 0, 1, 1)));
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => FocusLearn();
        WatchDetail(viewModel.Detail);
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VocabViewModel.IsLearning):
                Dispatcher.BeginInvoke(FocusLearn);
                break;
            case nameof(VocabViewModel.Detail):
                WatchDetail(_viewModel.Detail);
                Dispatcher.BeginInvoke(ShowOpenedWord);
                break;
            case nameof(VocabViewModel.IsAddOpen):
                Dispatcher.BeginInvoke(() =>
                {
                    if (_viewModel.IsAddOpen) AddDialog.WordBox.Focus();
                    else RestoreFocus();
                });
                break;
            case nameof(VocabViewModel.IsAgentOpen) when !_viewModel.IsAgentOpen:
                Dispatcher.BeginInvoke(RestoreFocus);
                break;
        }
    }

    /// <summary>Add text opens with the cursor in its text box; focus returns to the page when it closes.</summary>
    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VocabTextsViewModel.IsOpen)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.Texts.IsOpen) TextsDialog.BodyBox.Focus();
            else RestoreFocus();
        });
    }

    /// <summary>The opened word's Edit and Ask for a new analysis dialogs: the cursor goes in when one opens, back when it closes.</summary>
    private void WatchDetail(VocabWordDetailViewModel? detail)
    {
        if (_watchedDetail is not null)
        {
            _watchedDetail.PropertyChanged -= OnDetailChanged;
            _watchedDetail.Editor.PropertyChanged -= OnEditorChanged;
        }
        _watchedDetail = detail;
        if (detail is null) return;
        detail.PropertyChanged += OnDetailChanged;
        detail.Editor.PropertyChanged += OnEditorChanged;
    }

    private void OnDetailChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VocabWordDetailViewModel.IsAskingRedo) || sender is not VocabWordDetailViewModel detail) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (detail.IsAskingRedo) RedoDialog.NoteBox.Focus();
            else RestoreFocus();
        });
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VocabWordEditor.IsOpen) || sender is not VocabWordEditor editor) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (editor.IsOpen) EditDialog.PhoneticBox.Focus();
            else RestoreFocus();
        });
    }

    /// <summary>
    /// A family opened from the list comes into view at the top and takes the keyboard (Esc, ← →); the same family
    /// rebuilt after a change stays where it is.
    /// </summary>
    private void OnRootsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VocabRootsViewModel.Selected)) return;
        var id = _viewModel.Roots.Selected?.PartId;
        if (id == _shownPartId) return;
        _shownPartId = id;
        if (id is null || !_viewModel.IsRootsTab) return;
        Dispatcher.BeginInvoke(() =>
        {
            RootsView.RootsPanel.BringIntoView(new Rect(0, 0, 1, 1));
            if (Keyboard.FocusedElement is not TextBoxBase) RootsView.RootsPanel.Focus();
        });
    }

    /// <summary>
    /// A word opened from the list comes into view at the top and takes the keyboard (Esc, ← →; the row clicked is gone).
    /// Only when another word opens: the same word rebuilt after a change (a sync, a part added as a word) stays where
    /// the user is reading.
    /// </summary>
    private void ShowOpenedWord()
    {
        var id = _viewModel.Detail?.Id;
        if (id == _shownWordId) return;
        _shownWordId = id;
        if (id is null || !_viewModel.IsWordsTab || !IsVisible) return;
        WordView.DetailPanel.BringIntoView(new Rect(0, 0, 1, 1));
        if (Keyboard.FocusedElement is not TextBoxBase) WordView.DetailPanel.Focus();
    }

    /// <summary>A Type card opens with the cursor in its box; once checked, the keys answer again.</summary>
    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VocabCardViewModel.IsTyping)) Dispatcher.BeginInvoke(FocusLearn);
    }

    /// <summary>The keys reach the card only while focus is on the page, so the Learn tab takes it when shown.</summary>
    private void FocusLearn()
    {
        if (!_viewModel.IsLearning || !IsVisible || IsDialogOpen) return;
        if (_viewModel.Card.IsTyping) LearnView.TypeBox.Focus();
        else LearnView.LearnPanel.Focus();
    }

    /// <summary>After a dialog closes, the keys go back to the tab in front.</summary>
    private void RestoreFocus()
    {
        if (IsDialogOpen || !IsVisible) return;
        if (_viewModel.IsLearning) FocusLearn();
        else if (_viewModel.IsWordsTab && _viewModel.HasDetail) WordView.DetailPanel.Focus();
        else if (_viewModel.IsRootsTab) RootsView.RootsPanel.Focus();
    }

    private bool IsDialogOpen =>
        _viewModel.IsAddOpen || _viewModel.IsAgentOpen || _viewModel.Texts.IsOpen || _viewModel.Detail is { Editor.IsOpen: true } or { IsAskingRedo: true };

    /// <summary>Esc: the dialog in front closes (an edit is dropped, unsaved).</summary>
    private bool CloseDialog()
    {
        var vm = _viewModel;
        if (vm.Detail is { Editor.IsOpen: true } edited) edited.Editor.CancelCommand.Execute(null);
        else if (vm.Detail is { IsAskingRedo: true } redo) redo.BeginRedoCommand.Execute(null);
        else if (vm.Texts.IsOpen && vm.Texts.IsDetailOpen) vm.Texts.BackCommand.Execute(null);
        else if (vm.Texts.IsOpen) vm.Texts.CloseCommand.Execute(null);
        else if (vm.IsAddOpen) vm.CloseAddCommand.Execute(null);
        else if (vm.IsAgentOpen) vm.CloseAgentCommand.Execute(null);
        else return false;
        return true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_module.IsEnabled) return;
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && CloseDialog())
        {
            e.Handled = true;
            return;
        }
        // A dialog keeps its keys (Enter adds, Space types).
        if (IsDialogOpen || Keyboard.Modifiers != ModifierKeys.None) return;
        // Typing in a box, choosing in a list or flipping a switch keeps its keys.
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or ToggleButton) return;
        var command = _viewModel.Tab switch
        {
            VocabTab.Learn => LearnKey(e.Key),
            VocabTab.Words when _viewModel.HasDetail => e.Key switch
            {
                Key.Escape => _viewModel.CloseDetailCommand,
                Key.Right => _viewModel.NextWordCommand,
                Key.Left => _viewModel.PreviousWordCommand,
                _ => null,
            },
            VocabTab.Roots => RootsKey(e.Key),
            _ => null,
        };
        if (command is null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    private ICommand? LearnKey(Key key)
    {
        var card = _viewModel.Card;
        return key switch
        {
            Key.Space or Key.Enter when !card.IsRevealed => card.RevealCommand,
            Key.D1 or Key.NumPad1 => card.ForgotCommand,
            Key.D2 or Key.NumPad2 => card.RememberedCommand,
            Key.L => card.SayCommand,
            Key.S => card.SkipCommand,
            Key.K => card.KnowCommand,
            _ => null,
        };
    }

    /// <summary>An open family walks the list; a practised root shows its meaning, then the next root.</summary>
    private ICommand? RootsKey(Key key)
    {
        var roots = _viewModel.Roots;
        if (roots.HasSelected)
            return key switch
            {
                Key.Escape => roots.CloseSelectedCommand,
                Key.Right => roots.NextFamilyCommand,
                Key.Left => roots.PreviousFamilyCommand,
                _ => null,
            };
        if (!roots.HasPractice) return null;
        return key switch
        {
            Key.Space or Key.Enter => roots.IsPracticeRevealed ? roots.NextRootCommand : roots.RevealRootCommand,
            Key.Escape => roots.ClosePracticeCommand,
            _ => null,
        };
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(VocabPage));

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        HeaderActions.IsEnabled = on;
        Tabs.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
