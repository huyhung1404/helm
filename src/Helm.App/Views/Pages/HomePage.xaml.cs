using System.Windows.Controls;
using Helm.App.ViewModels;

namespace Helm.App.Views.Pages;

internal partial class HomePage : Page
{
    public HomePage(HomeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
