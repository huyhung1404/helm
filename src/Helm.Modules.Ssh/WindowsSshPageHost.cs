using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Helm.Core.Settings;
using Helm.Modules.Ssh.Terminal;
using Helm.Modules.Ssh.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Ssh;

/// <summary>
/// What only Windows does for the shared <see cref="SshView"/>: the terminal (xterm.js in WebView2,
/// <see cref="WindowsTerminal"/>), the key file picker and OpenSSH's folder. While the tool is off the servers stay
/// visible but nothing connects.
/// </summary>
internal sealed class WindowsSshPageHost : ISshPageHost
{
    private readonly IClipboardService _clipboard;
    private readonly ILogger<WindowsSshPageHost> _logger;
    private readonly string _webViewFolder;

    public WindowsSshPageHost(SshModule module, SshViewModel viewModel, IClipboardService clipboard, ISettingsStoreFactory settings,
        ILogger<WindowsSshPageHost> logger)
    {
        _clipboard = clipboard;
        _logger = logger;
        _webViewFolder = Path.Combine(settings.Paths.Root, "cache", SshIds.ModuleId, "webview2");
        View = new SshView(module, viewModel, this);
    }

    /// <summary>The page the shell shows.</summary>
    public Control View { get; }

    public ISshTerminal CreateTerminal(SshViewModel viewModel) => new WindowsTerminal(viewModel, _clipboard, _logger, _webViewFolder);

    /// <summary>Where OpenSSH keeps its config, keys and known_hosts on Windows (%USERPROFILE%\.ssh).</summary>
    public string? OpenSshFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public bool CanUseKeyFiles => true;

    public string KeyProtection => "Its private half is encrypted for your Windows account and never leaves this PC.";

    /// <summary>The system file picker. It only picks a path; the key is read when connecting.</summary>
    public async Task<string?> PickKeyFileAsync()
    {
        if (TopLevel.GetTopLevel(View)?.StorageProvider is not { } storage) return null;
        var folder = OpenSshFolder;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the private key file",
            SuggestedStartLocation = folder is not null && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder).ConfigureAwait(true) : null,
            FileTypeFilter = [new FilePickerFileType("Private keys") { Patterns = ["*.*"] }],
        }).ConfigureAwait(true);
        return files.Count > 0 && files[0].TryGetLocalPath() is { } path && File.Exists(path) ? path : null;
    }
}
