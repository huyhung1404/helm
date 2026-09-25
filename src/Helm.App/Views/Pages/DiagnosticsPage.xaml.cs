using System.Windows.Controls;
using Helm.App.ViewModels;

namespace Helm.App.Views.Pages;

internal partial class DiagnosticsPage : Page
{
    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
