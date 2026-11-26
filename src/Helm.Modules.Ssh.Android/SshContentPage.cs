using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Ui;
using Helm.Modules.Ssh.Terminal;
using Helm.Modules.Ssh.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// <see cref="SshView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>: the terminal takes the
/// height that is left and shrinks when the keyboard opens). This page gives it what only the phone has: the terminal (Android's
/// WebView, <see cref="TerminalHost"/>, a native control the page steps aside for while a sheet is open) and the drawer button.
/// Back closes the sheet on top first (see <see cref="SshModule.HandleBack"/>). The page is transient; sessions live in the view model.
/// </summary>
public sealed class SshContentPage : UserControl, IFullPage, ISshPageHost
{
    private readonly IClipboardService _clipboard;
    private readonly SshView _view;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public SshContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<SshModule>(), HelmAndroidServices.Current.GetRequiredService<SshViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IClipboardService>())
    {
    }

    public SshContentPage(SshModule module, SshViewModel viewModel, IClipboardService clipboard)
    {
        _clipboard = clipboard;
        _view = new SshView(module, viewModel, this);
        Content = _view;
    }

    public string KeyProtection => "Its private half is protected by the Android Keystore and never leaves the phone.";

    public Action<Control>? OpenMenu => ShellChrome.OpenNavigation;

    public ISshTerminal CreateTerminal(SshViewModel viewModel) => new TerminalHost(viewModel, _clipboard);
}
