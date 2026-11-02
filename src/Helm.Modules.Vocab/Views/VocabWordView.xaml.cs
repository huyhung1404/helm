using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Vocab.Views;

/// <summary>
/// A word's page on the Words tab (see the XAML). How well the word is known (its dots) and whether its pinyin keeps
/// slipping come from the quiz, read again whenever the page's word changes or is rebuilt after a change.
/// </summary>
public partial class VocabWordView : UserControl
{
    public static readonly DependencyProperty DotsProperty =
        DependencyProperty.Register(nameof(Dots), typeof(IReadOnlyList<bool>), typeof(VocabWordView), new PropertyMetadata(Array.Empty<bool>()));

    public static readonly DependencyProperty IsSlippingProperty =
        DependencyProperty.Register(nameof(IsSlipping), typeof(bool), typeof(VocabWordView), new PropertyMetadata(false));

    private VocabViewModel? _viewModel;
    private VocabQuiz? _quiz;

    public VocabWordView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as VocabViewModel);
    }

    /// <summary>One flag per level, filled up to the word's (the page's dots).</summary>
    public IReadOnlyList<bool> Dots
    {
        get => (IReadOnlyList<bool>)GetValue(DotsProperty);
        set => SetValue(DotsProperty, value);
    }

    /// <summary>A Chinese word whose pinyin keeps slipping while its meaning is known.</summary>
    public bool IsSlipping
    {
        get => (bool)GetValue(IsSlippingProperty);
        set => SetValue(IsSlippingProperty, value);
    }

    /// <summary>The answers so far (set by the page): the word's level and its pinyin slips.</summary>
    public VocabQuiz? Quiz
    {
        get => _quiz;
        set
        {
            _quiz = value;
            ReadStanding();
        }
    }

    private void Watch(VocabViewModel? viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
        ReadStanding();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VocabViewModel.Detail)) ReadStanding();
    }

    private void ReadStanding()
    {
        if (_viewModel?.Detail is not { } detail || Quiz is null)
        {
            Dots = [];
            IsSlipping = false;
            return;
        }
        var stats = Quiz.StatsOf(detail.Id);
        Dots = [.. Enumerable.Range(0, VocabQuiz.TopLevel).Select(i => i < stats.Level)];
        IsSlipping = !detail.Word.Hidden && VocabQuiz.IsSlipping(detail.Word, stats);
    }

    /// <summary>Delete… asks first (the page's confirmation).</summary>
    private void Delete_Click(object sender, RoutedEventArgs e) => _viewModel?.DeleteWordCommand.Execute(null);
}
