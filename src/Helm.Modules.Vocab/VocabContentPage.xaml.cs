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
/// card puts the cursor in its box, and Enter there checks the word.
/// </summary>
public partial class VocabContentPage : Page
{
    private readonly VocabModule _module;
    private readonly VocabViewModel _viewModel;

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
        if (!_viewModel.IsLearning || !_module.IsEnabled || Keyboard.Modifiers != ModifierKeys.None) return;
        // Typing in a box or choosing in a list keeps its keys.
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem) return;
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
