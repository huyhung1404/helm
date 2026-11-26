using System.Windows;
using Helm.Core.Ui;

namespace Helm.Modules.SlackBot;

public partial class SlackBotPage : ModulePageBase
{
    private readonly SlackBotViewModel _viewModel;

    public SlackBotPage(SlackBotViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Open on the steps until the app is connected.
        AppExpander.IsExpanded = !viewModel.HasTokens;
    }

    private void CopyManifest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.Manifest);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy; the button can be pressed again.
        }
    }

    /// <summary>Password boxes do not bind: the tokens are read here, handed over once and cleared.</summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await _viewModel.ConnectAsync(UserTokenBox.Password, AppTokenBox.Password)) return;
            UserTokenBox.Password = "";
            AppTokenBox.Password = "";
            AppExpander.IsExpanded = false;
        }
        catch (Exception ex)
        {
            _viewModel.TokenProblem = "Could not save the tokens: " + ex.Message;
        }
    }
}
