using Helm.Shell.Windows;

namespace Helm.App.Services;

/// <summary>What Home and General ask of the app's tabs and process (see <see cref="IShellWindowsHost"/>).</summary>
internal sealed class ShellWindowsHost(IShellNavigator navigator, ShellController shell) : IShellWindowsHost
{
    public void OpenPage(Type pageType) => navigator.Navigate(pageType);

    public void OpenGeneral() => navigator.Navigate(typeof(GeneralPage));

    public void OpenWhatsNew() => navigator.Navigate(typeof(Views.Pages.WhatsNewPage));

    public Task RestartAsync() => shell.RestartAsync();
}
