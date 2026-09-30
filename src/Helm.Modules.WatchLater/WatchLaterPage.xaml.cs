using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.WatchLater;

/// <summary>Watch Later's settings (Home → Utilities). The saved videos are on <see cref="WatchLaterContentPage"/>.</summary>
public partial class WatchLaterPage : ModulePageBase
{
    private readonly WatchLaterToolsViewModel _tools;
    private readonly IShellNavigation _navigation;

    public WatchLaterPage(WatchLaterModule module, WatchLaterViewModel viewModel, WatchLaterToolsViewModel tools, IShellNavigation navigation)
    {
        _tools = tools;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The downloads part is Windows-only and has its own view model.
        ToolsPanel.DataContext = tools;
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(WatchLaterContentPage));

    /// <summary>View glue: the folder dialog is Windows UI.</summary>
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save downloaded videos in", InitialDirectory = Directory.Exists(_tools.Folder) ? _tools.Folder : null };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _tools.SetFolder(dialog.FolderName);
    }
}
