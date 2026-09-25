using System.Windows.Controls;
using Helm.App.Services;

namespace Helm.App.Views.Pages;

internal partial class WhatsNewPage : Page
{
    public WhatsNewPage()
    {
        InitializeComponent();
        VersionText.Text = $"Helm {AppInfo.Version}";
    }
}
