using System.Windows.Controls;

namespace Helm.Modules.QuickCapture;

/// <summary>Quick Capture's settings (Home → Utilities): the Avalonia <see cref="QuickCaptureView"/> inside this WPF page.</summary>
public partial class QuickCapturePage : Page
{
    public QuickCapturePage(QuickCaptureViewModel viewModel)
    {
        InitializeComponent();
        Host.Show(() => new QuickCaptureView(viewModel));
    }
}
