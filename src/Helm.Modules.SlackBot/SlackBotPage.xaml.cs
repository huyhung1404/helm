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

    /// <summary>Adds project folders to a conversation (several may be picked at once).</summary>
    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SlackChannelRow row) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Projects Lumi may read for {row.Name}", Multiselect = true };
        if (row.Projects.LastOrDefault(System.IO.Directory.Exists) is { } last) dialog.InitialDirectory = System.IO.Path.GetDirectoryName(last);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var folder in dialog.FolderNames) row.AddProject(folder);
    }

    /// <summary>A conversation's model and effort, picked from a menu like Claude Code's in VS Code.</summary>
    private void AiMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlackChannelRow row } button) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        Add("Model", SlackAiOptions.Models, row.Model, value => row.Model = value);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Effort", SlackAiOptions.Efforts, row.Effort, value => row.Effort = value);
        menu.IsOpen = true;

        void Add(string title, IReadOnlyList<SlackBotChoice<string>> choices, string current, Action<string> pick)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = title, IsEnabled = false });
            foreach (var choice in choices)
            {
                var item = new System.Windows.Controls.MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = choice.Value == current };
                item.Click += (_, _) => pick(choice.Value);
                menu.Items.Add(item);
            }
        }
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string folder, Tag: SlackChannelRow row }) row.RemoveProject(folder);
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
