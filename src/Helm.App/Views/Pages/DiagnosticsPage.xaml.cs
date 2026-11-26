using System.Windows.Controls;
using Helm.Shell.Diagnostics;
using Helm.Shell.Windows;

namespace Helm.App.Views.Pages;

/// <summary>Diagnostics: the shared Avalonia DiagnosticsView (Helm.Shell.UI) inside this WPF page.</summary>
internal partial class DiagnosticsPage : Page
{
    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        ShellViews.ShowDiagnostics(Host, viewModel);
    }
}
