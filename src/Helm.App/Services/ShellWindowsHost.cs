using Helm.Shell.Windows;

namespace Helm.App.Services;

/// <summary>What Home and General ask of the app's tabs and process (see <see cref="IShellWindowsHost"/>).</summary>
internal sealed class ShellWindowsHost(IShellNavigator navigator, ShellController shell, Core.Services.IProcessLauncher launcher) : IShellWindowsHost
{
    public void OpenPage(Type pageType) => navigator.Navigate(pageType);

    public void OpenGeneral() => navigator.Navigate(typeof(GeneralPage));

    /// <summary>This version's release notes on GitHub (the menu has no "What's new" page any more).</summary>
    public void OpenWhatsNew() => launcher.OpenUrl(Shell.Services.AppInfo.ReleaseNotesUrl);

    public Task RestartAsync() => shell.RestartAsync();
}
