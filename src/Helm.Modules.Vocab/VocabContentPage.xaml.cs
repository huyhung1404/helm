using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Helm.Core.Modules;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the words stay visible but read-only, with a note pointing to the settings (Home → Utilities). On the Learn
/// tab the keyboard answers: Space shows the answer, 1 Forgot, 2 Remembered, L listens, S skips, K "I know it"; a Type
/// card puts the cursor in its box, and Enter there checks the word. A word opened on the Words tab takes the page:
/// Esc goes back to the list, ← and → open the word before and after it; the same for a family on the Roots tab.
/// </summary>
public partial class VocabContentPage : Page
{
    private readonly VocabModule _module;
    private readonly VocabViewModel _viewModel;
    private string? _shownWordId;
    private string? _shownPartId;

    public VocabContentPage(VocabModule module, VocabViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Card.PropertyChanged += OnCardChanged;
        viewModel.Roots.PropertyChanged += OnRootsChanged;
        // A new page starts at the top of its list.
        viewModel.List.Pages.Turned += (_, _) => Dispatcher.BeginInvoke(() => WordsList.BringIntoView(new Rect(0, 0, 1, 1)));
        viewModel.Roots.Pages.Turned += (_, _) => Dispatcher.BeginInvoke(() => RootsPanel.BringIntoView(new Rect(0, 0, 1, 1)));
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => FocusLearn();
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VocabViewModel.IsLearning)) Dispatcher.BeginInvoke(FocusLearn);
        if (e.PropertyName == nameof(VocabViewModel.Detail)) Dispatcher.BeginInvoke(ShowOpenedWord);
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
            RootsPanel.BringIntoView(new Rect(0, 0, 1, 1));
            if (Keyboard.FocusedElement is not TextBoxBase) RootsPanel.Focus();
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
        DetailPanel.BringIntoView(new Rect(0, 0, 1, 1));
        if (Keyboard.FocusedElement is not TextBoxBase) DetailPanel.Focus();
    }

    /// <summary>A Type card opens with the cursor in its box; once checked, the keys answer again.</summary>
    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VocabCardViewModel.IsTyping)) Dispatcher.BeginInvoke(FocusLearn);
    }

    /// <summary>The keys reach the card only while focus is on the page, so the Learn tab takes it when shown.</summary>
    private void FocusLearn()
    {
        if (!_viewModel.IsLearning || !IsVisible) return;
        if (_viewModel.Card.IsTyping) TypeBox.Focus();
        else LearnPanel.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_module.IsEnabled || Keyboard.Modifiers != ModifierKeys.None) return;
        // Typing in a box or choosing in a list keeps its keys.
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem) return;
        var roots = _viewModel.Roots;
        if (_viewModel.IsWordsTab && _viewModel.HasDetail || _viewModel.IsRootsTab && roots.HasSelected)
        {
            var words = _viewModel.IsWordsTab;
            var step = e.Key switch
            {
                Key.Escape => words ? _viewModel.CloseDetailCommand : roots.CloseSelectedCommand,
                Key.Right => words ? _viewModel.NextWordCommand : roots.NextFamilyCommand,
                Key.Left => words ? _viewModel.PreviousWordCommand : roots.PreviousFamilyCommand,
                _ => null,
            };
            if (step is null || !step.CanExecute(null)) return;
            step.Execute(null);
            e.Handled = true;
            return;
        }
        if (!_viewModel.IsLearning) return;
        var card = _viewModel.Card;
        var command = e.Key switch
        {
            Key.Space or Key.Enter when !card.IsRevealed => card.RevealCommand,
            Key.D1 or Key.NumPad1 => card.ForgotCommand,
            Key.D2 or Key.NumPad2 => card.RememberedCommand,
            Key.L => card.SayCommand,
            Key.S => card.SkipCommand,
            Key.K => card.KnowCommand,
            _ => null,
        };
        if (command is null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
