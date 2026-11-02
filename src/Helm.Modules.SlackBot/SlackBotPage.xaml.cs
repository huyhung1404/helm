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
        TelegramExpander.IsExpanded = !viewModel.TelegramConnected;
    }

    /// <summary>The token is read from the password box here, handed over once and cleared.</summary>
    private void SaveTelegramToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.SaveTelegramToken(TelegramTokenBox.Password)) TelegramTokenBox.Password = "";
        }
        catch (Exception ex)
        {
            _viewModel.TelegramStatus = "Could not save the token: " + ex.Message;
        }
    }

    /// <summary>Picks the project folder of a conversation.</summary>
    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SlackChannelRow row) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Project Lumi may read for {row.Name}" };
        if (row.Project.Length > 0 && System.IO.Directory.Exists(row.Project)) dialog.InitialDirectory = row.Project;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) row.Project = dialog.FolderName;
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
