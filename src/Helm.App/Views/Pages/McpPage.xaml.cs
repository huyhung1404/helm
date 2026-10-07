using System.Windows.Controls;
using Helm.App.ViewModels;

namespace Helm.App.Views.Pages;

internal partial class McpPage : Page
{
    public McpPage(McpViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
