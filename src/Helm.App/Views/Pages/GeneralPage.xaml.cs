using System.Windows.Controls;
using Helm.App.ViewModels;

namespace Helm.App.Views.Pages;

internal partial class GeneralPage : Page
{
    public GeneralPage(GeneralViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
