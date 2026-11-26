using Helm.Shell.Diagnostics;
using Helm.Shell.Pages;
using Helm.Ui.Windows;

namespace Helm.Shell.Windows;

/// <summary>
/// The shell's small shared pages (Welcome, Diagnostics) for Helm.App's WPF pages, which hold no Avalonia types: each
/// shows its Helm.Shell.UI view in the page's <see cref="AvaloniaHost"/>.
/// </summary>
public static class ShellViews
{
    /// <summary>Welcome to Helm; its button opens Home (the tools are turned on in its Utilities).</summary>
    public static void ShowWelcome(AvaloniaHost host, IShellWindowsHost shell) =>
        host.Show(() => new WelcomeView(() => shell.OpenPage(typeof(HomePage))));

    public static void ShowDiagnostics(AvaloniaHost host, DiagnosticsViewModel viewModel) =>
        host.Show(() => new DiagnosticsView(viewModel));
}
