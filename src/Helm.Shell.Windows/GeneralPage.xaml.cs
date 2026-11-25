using System.Windows.Controls;
using Helm.General;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Shell.Windows;

/// <summary>General, in parts: the shared Avalonia <see cref="GeneralView"/> inside this WPF page.</summary>
public partial class GeneralPage : Page
{
    public GeneralPage(IServiceProvider services)
    {
        InitializeComponent();
        Host.Show(() => new GeneralView(services.GetRequiredService<GeneralComposer>().Model));
    }
}
