using Avalonia;
using Avalonia.Controls;
using Helm.Shell.Diagnostics;
using Helm.Shell.Services;

namespace Helm.Shell.Pages;

/// <summary>
/// Diagnostics (see the .axaml): Helm as it sees this computer, every part copyable for a bug report. The hooks test
/// stops when the page is closed.
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private const double Narrow = 720;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public DiagnosticsView() => InitializeComponent();

    public DiagnosticsView(DiagnosticsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        TopLine.Text = $"HELM {AppInfo.Version} · FOR BUG REPORTS";
        AttachedToVisualTree += (_, _) => viewModel.RefreshCommand.Execute(null);
        DetachedFromVisualTree += (_, _) => viewModel.IsHookTestEnabled = false;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
    }
}
