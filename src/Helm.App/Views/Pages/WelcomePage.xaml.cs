using System.Windows.Controls;
using Helm.Shell.Windows;

namespace Helm.App.Views.Pages;

/// <summary>Welcome to Helm: the shared Avalonia WelcomeView (Helm.Shell.UI) inside this WPF page.</summary>
internal partial class WelcomePage : Page
{
    public WelcomePage(IShellWindowsHost shell)
    {
        InitializeComponent();
        ShellViews.ShowWelcome(Host, shell);
    }
}
