using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Ssh;

/// <summary>SSH's settings (Home → Utilities). Connecting and the terminal are on <see cref="SshContentPage"/>.</summary>
public partial class SshPage : ModulePageBase
{
    private readonly IShellNavigation _navigation;
    private readonly SshViewModel _viewModel;

    /// <summary>Where OpenSSH keeps its config, keys and known_hosts on Windows (%USERPROFILE%\.ssh).</summary>
    internal static string OpenSshFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public SshPage(SshModule module, SshViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
        // Back from Vault (unlocked there): the list of Vault fields in the editor is read again.
        Loaded += (_, _) => viewModel.OnSettingsShown();
    }

    private void OpenSsh_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(SshContentPage));

    private void Import_Click(object sender, RoutedEventArgs e) => _viewModel.ImportOpenSsh(OpenSshFolder);

    /// <summary>View glue: the file dialog is Windows UI. It only picks a path; the key is read when connecting.</summary>
    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the private key file",
            InitialDirectory = Directory.Exists(OpenSshFolder) ? OpenSshFolder : "",
            Filter = "Private keys|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _viewModel.EditKeyFile = dialog.FileName;
    }
}
