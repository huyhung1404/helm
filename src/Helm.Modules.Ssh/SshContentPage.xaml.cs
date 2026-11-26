using System.Windows;
using System.Windows.Controls;
using Helm.Core.Settings;
using Helm.Modules.Ssh.Terminal;
using Helm.Modules.Ssh.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the shared Avalonia
/// <see cref="SshView"/> inside this WPF page, plus what only Windows does for it: the terminal (xterm.js in WebView2,
/// <see cref="WindowsTerminal"/>), the key file dialog and OpenSSH's folder. While the tool is off the servers stay visible
/// but nothing connects.
/// </summary>
public partial class SshContentPage : Page, ISshPageHost
{
    private readonly IClipboardService _clipboard;
    private readonly ILogger<SshContentPage> _logger;
    private readonly string _webViewFolder;

    public SshContentPage(SshModule module, SshViewModel viewModel, IClipboardService clipboard, ISettingsStoreFactory settings,
        ILogger<SshContentPage> logger)
    {
        _clipboard = clipboard;
        _logger = logger;
        _webViewFolder = System.IO.Path.Combine(settings.Paths.Root, "cache", SshIds.ModuleId, "webview2");
        InitializeComponent();
        Host.Show(() => new SshView(module, viewModel, this));
    }

    public ISshTerminal CreateTerminal(SshViewModel viewModel) => new WindowsTerminal(viewModel, _clipboard, _logger, _webViewFolder);

    /// <summary>Where OpenSSH keeps its config, keys and known_hosts on Windows (%USERPROFILE%\.ssh).</summary>
    public string? OpenSshFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public bool CanUseKeyFiles => true;

    public string KeyProtection => "Its private half is encrypted for your Windows account and never leaves this PC.";

    /// <summary>The file dialog is Windows UI. It only picks a path; the key is read when connecting.</summary>
    public Task<string?> PickKeyFileAsync()
    {
        var folder = OpenSshFolder;
        var dialog = new OpenFileDialog
        {
            Title = "Choose the private key file",
            InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : "",
            Filter = "Private keys|*.*",
            CheckFileExists = true,
        };
        return Task.FromResult(dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null);
    }
}
