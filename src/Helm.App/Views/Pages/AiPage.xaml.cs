using System.Windows.Controls;
using Helm.App.ViewModels;

namespace Helm.App.Views.Pages;

internal partial class AiPage : Page
{
    public AiPage(AiViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // The clients' config files and the modules may have changed while the page was away.
        Loaded += async (_, _) =>
        {
            viewModel.Agents.Refresh();
            await viewModel.Keys.OnShownAsync();
        };
    }
}
