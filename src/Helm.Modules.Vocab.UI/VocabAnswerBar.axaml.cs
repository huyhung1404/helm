using System.ComponentModel;
using Avalonia.Controls;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab.Ui;

/// <summary>
/// The answer bar at the foot of the Vocabulary page (see the XAML); DataContext: <see cref="VocabViewModel"/>. It
/// shows on the Learn tab while there is a card, and on the Roots tab while a root is practised; <see cref="Shown"/>
/// tells the page to keep room for it under the content.
/// </summary>
public partial class VocabAnswerBar : UserControl
{
    private VocabViewModel? _vm;

    public VocabAnswerBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch();
    }

    /// <summary>Raised when the bar comes or goes.</summary>
    public event EventHandler? Shown;

    private void Watch()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.Card.PropertyChanged -= OnChanged;
            _vm.Roots.PropertyChanged -= OnChanged;
        }
        _vm = DataContext as VocabViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnChanged;
        _vm.Card.PropertyChanged += OnChanged;
        _vm.Roots.PropertyChanged += OnChanged;
        Update();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabViewModel.Tab) or nameof(VocabCardViewModel.HasWord) or nameof(VocabCardViewModel.IsRevealed)
            or nameof(VocabRootsViewModel.Practice) or nameof(VocabRootsViewModel.Selected) or nameof(VocabRootsViewModel.HasPractice))
            Update();
    }

    private void Update()
    {
        if (_vm is not { } vm) return;
        var learn = vm.IsLearning && vm.Card.HasWord;
        var practice = vm.IsRootsTab && vm.Roots.HasPractice && vm.Roots.ShowsList;
        var was = IsVisible;
        var row = AnswerRow.IsVisible;
        AskRow.IsVisible = learn && !vm.Card.IsRevealed;
        AnswerRow.IsVisible = learn && vm.Card.IsRevealed;
        PracticeRow.IsVisible = practice;
        IsVisible = learn || practice;
        if (IsVisible && !was) Motion.Enter(Bar, 0.96);
        else if (AnswerRow.IsVisible && !row) Motion.Enter(AnswerRow, 0.97);
        if (IsVisible != was) Shown?.Invoke(this, EventArgs.Empty);
    }
}
