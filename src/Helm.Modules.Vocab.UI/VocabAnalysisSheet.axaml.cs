using System.ComponentModel;
using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>The Analysis sheet (see the XAML); open while <see cref="VocabViewModel.IsAgentOpen"/>.</summary>
public partial class VocabAnalysisSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public VocabAnalysisSheet() => InitializeComponent();

    public VocabAnalysisSheet(VocabViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        CloseButton.Click += (_, _) => viewModel.CloseAgentCommand.Execute(null);
        DoneButton.Click += (_, _) => viewModel.CloseAgentCommand.Execute(null);
        void Others()
        {
            var others = VocabWork.Others(viewModel);
            OthersList.ItemsSource = others;
            OthersCard.IsVisible = others.Count > 0;
        }
        void OnChanged(object? s, PropertyChangedEventArgs e) => Others();
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnChanged;
            Others();
        };
        DetachedFromVisualTree += (_, _) => viewModel.PropertyChanged -= OnChanged;
    }
}
